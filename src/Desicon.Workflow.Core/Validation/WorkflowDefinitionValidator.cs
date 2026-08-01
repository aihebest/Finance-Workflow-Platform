using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Guards;

namespace Desicon.Workflow.Core.Validation;

public sealed record ValidationFinding(string Severity, string Code, string Message);

public sealed class ValidationResult
{
    private readonly List<ValidationFinding> _findings = new();

    public IReadOnlyList<ValidationFinding> Findings => _findings;

    public bool IsValid => _findings.All(f => f.Severity != "Error");

    public void Error(string code, string message) =>
        _findings.Add(new ValidationFinding("Error", code, message));

    public void Warning(string code, string message) =>
        _findings.Add(new ValidationFinding("Warning", code, message));
}

/// <summary>
/// Validates a workflow definition before it is published.
///
/// This exists because a structurally broken definition is a live incident,
/// not a unit-test failure. A state with no way out silently strands every
/// request that reaches it, and nobody notices until an approver asks why
/// their claim has not moved in three weeks. That class of defect is caught
/// here, in CI, against the real definition files.
/// </summary>
public sealed class WorkflowDefinitionValidator
{
    private static readonly HashSet<string> KnownResolvers = new(StringComparer.Ordinal)
    {
        "Requester",
        "Beneficiary",
        "LineManagerOf",
        "DepartmentHeadOf",
        "ProjectManagerOf",
        "CurrentActor"
    };

    public static ValidationResult Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var result = new ValidationResult();
        var stateKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var state in definition.States)
        {
            if (!stateKeys.Add(state.Key))
            {
                result.Error("DUPLICATE_STATE", $"State '{state.Key}' is declared more than once.");
            }
        }

        ValidateInitialState(definition, result);
        ValidateTerminalStates(definition, result);
        ValidateTransitionEndpoints(definition, stateKeys, result);
        ValidateActors(definition, result);
        ValidateGuards(definition, result);
        ValidateSla(definition, stateKeys, result);
        ValidateReachability(definition, result);
        ValidateTerminalAttainability(definition, result);
        ValidateRejectionPaths(definition, result);

        return result;
    }

    private static void ValidateInitialState(WorkflowDefinition definition, ValidationResult result)
    {
        var initialStates = definition.States.Where(s => s.Type == StateType.Initial).ToList();

        if (initialStates.Count == 0)
        {
            result.Error("NO_INITIAL_STATE", "Definition has no initial state.");
        }
        else if (initialStates.Count > 1)
        {
            result.Error(
                "MULTIPLE_INITIAL_STATES",
                $"Definition has {initialStates.Count} initial states: " +
                string.Join(", ", initialStates.Select(s => s.Key)));
        }
    }

    private static void ValidateTerminalStates(WorkflowDefinition definition, ValidationResult result)
    {
        if (!definition.States.Any(s => s.Type == StateType.Terminal))
        {
            result.Error("NO_TERMINAL_STATE", "Definition has no terminal state.");
        }

        foreach (var terminal in definition.States.Where(s => s.Type == StateType.Terminal))
        {
            if (definition.TransitionsFrom(terminal.Key).Any())
            {
                result.Error(
                    "TERMINAL_HAS_EXIT",
                    $"Terminal state '{terminal.Key}' has outbound transitions.");
            }
        }
    }

    private static void ValidateTransitionEndpoints(
        WorkflowDefinition definition, HashSet<string> stateKeys, ValidationResult result)
    {
        foreach (var transition in definition.Transitions)
        {
            if (!stateKeys.Contains(transition.From))
            {
                result.Error(
                    "UNKNOWN_FROM_STATE",
                    $"Transition '{transition.Action}' references unknown from-state '{transition.From}'.");
            }

            if (!stateKeys.Contains(transition.To))
            {
                result.Error(
                    "UNKNOWN_TO_STATE",
                    $"Transition '{transition.Action}' references unknown to-state '{transition.To}'.");
            }

            if (string.IsNullOrWhiteSpace(transition.Action))
            {
                result.Error("MISSING_ACTION", $"Transition {transition.From} -> {transition.To} has no action.");
            }

            if (transition.Guard is not null && string.IsNullOrWhiteSpace(transition.GuardMessage))
            {
                result.Warning(
                    "GUARD_WITHOUT_MESSAGE",
                    $"Transition '{transition.Action}' from '{transition.From}' has a guard but no " +
                    "guardMessage. A blocked user will see no explanation.");
            }
        }
    }

    private static void ValidateActors(WorkflowDefinition definition, ValidationResult result)
    {
        foreach (var transition in definition.Transitions)
        {
            if (!transition.Actor.IsValid)
            {
                result.Error(
                    "NO_ACTOR",
                    $"Transition '{transition.Action}' from '{transition.From}' specifies neither " +
                    "a role nor a resolver.");
                continue;
            }

            if (transition.Actor.Resolver is { } resolver && !KnownResolvers.Contains(resolver))
            {
                result.Error(
                    "UNKNOWN_RESOLVER",
                    $"Transition '{transition.Action}' uses unknown actor resolver '{resolver}'. " +
                    $"Known resolvers: {string.Join(", ", KnownResolvers.Order(StringComparer.Ordinal))}.");
            }
        }
    }

    private static void ValidateGuards(WorkflowDefinition definition, ValidationResult result)
    {
        foreach (var transition in definition.Transitions.Where(t => t.Guard is not null))
        {
            try
            {
                GuardParser.Parse(transition.Guard!);
            }
            catch (GuardSyntaxException ex)
            {
                result.Error(
                    "INVALID_GUARD",
                    $"Transition '{transition.Action}' from '{transition.From}' has an invalid " +
                    $"guard expression: {ex.Message}");
            }
        }
    }

    private static void ValidateSla(
        WorkflowDefinition definition, HashSet<string> stateKeys, ValidationResult result)
    {
        foreach (var state in definition.States.Where(s => s.Sla is not null))
        {
            var sla = state.Sla!;

            if (sla.Hours <= 0)
            {
                result.Error("INVALID_SLA", $"State '{state.Key}' has a non-positive SLA.");
            }

            if (sla.EscalateTo is { } target && !stateKeys.Contains(target))
            {
                result.Error(
                    "UNKNOWN_ESCALATION_TARGET",
                    $"State '{state.Key}' escalates to unknown state '{target}'.");
            }

            if (sla.ReminderEveryHours is { } reminder && reminder >= sla.Hours)
            {
                result.Warning(
                    "REMINDER_AFTER_BREACH",
                    $"State '{state.Key}' sends its first reminder at {reminder}h but breaches at " +
                    $"{sla.Hours}h, so the reminder arrives too late to prevent the breach.");
            }
        }

        var statesWithoutSla = definition.States
            .Where(s => s.Type is StateType.Normal && s.Sla is null)
            .Select(s => s.Key)
            .ToList();

        if (statesWithoutSla.Count > 0)
        {
            result.Warning(
                "STATE_WITHOUT_SLA",
                $"These states have no SLA and so cannot breach or escalate: " +
                string.Join(", ", statesWithoutSla));
        }
    }

    private static void ValidateReachability(WorkflowDefinition definition, ValidationResult result)
    {
        var initial = definition.States.FirstOrDefault(s => s.Type == StateType.Initial);

        if (initial is null)
        {
            return;
        }

        var reachable = Traverse(definition, initial.Key);

        foreach (var state in definition.States)
        {
            if (!reachable.Contains(state.Key))
            {
                result.Error(
                    "UNREACHABLE_STATE",
                    $"State '{state.Key}' cannot be reached from the initial state.");
            }
        }
    }

    private static void ValidateTerminalAttainability(
        WorkflowDefinition definition, ValidationResult result)
    {
        var terminals = definition.States
            .Where(s => s.Type == StateType.Terminal)
            .Select(s => s.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var state in definition.States.Where(s => s.Type != StateType.Terminal))
        {
            var reachable = Traverse(definition, state.Key);

            if (!reachable.Overlaps(terminals))
            {
                result.Error(
                    "DEAD_END_STATE",
                    $"No terminal state is reachable from '{state.Key}'. A request that enters " +
                    "this state can never be closed.");
            }
        }
    }

    private static void ValidateRejectionPaths(WorkflowDefinition definition, ValidationResult result)
    {
        var approvalStates = definition.States
            .Where(s => s.Type == StateType.Normal && s.Sla is not null)
            .Select(s => s.Key);

        foreach (var stateKey in approvalStates)
        {
            var outbound = definition.TransitionsFrom(stateKey).ToList();

            var hasNegativePath = outbound.Any(t =>
                t.Action is "REJECT" or "RETURN" or "WRITE_OFF");

            if (outbound.Count > 0 && !hasNegativePath)
            {
                result.Warning(
                    "NO_NEGATIVE_PATH",
                    $"State '{stateKey}' offers no REJECT or RETURN action. An approver who " +
                    "disagrees has nowhere to send the request.");
            }
        }
    }

    private static HashSet<string> Traverse(WorkflowDefinition definition, string startKey)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        queue.Enqueue(startKey);
        visited.Add(startKey);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var transition in definition.TransitionsFrom(current))
            {
                if (visited.Add(transition.To))
                {
                    queue.Enqueue(transition.To);
                }
            }
        }

        return visited;
    }
}
