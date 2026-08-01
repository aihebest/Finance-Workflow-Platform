using System.Text.Json.Serialization;

namespace Desicon.Workflow.Core.Definitions;

/// <summary>
/// A workflow definition is data, not code. Adding a new business process to
/// the platform means publishing one of these -- it must never require a
/// change to the engine assembly.
/// </summary>
public sealed class WorkflowDefinition
{
    public required string ModuleKey { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>QMS form code, e.g. DEL-AC-FRM-002.</summary>
    public required string FormCode { get; init; }

    /// <summary>QMS revision, e.g. "05". Stamped onto every request captured
    /// under this definition so a historical request reprints in its own
    /// layout after a later revision ships.</summary>
    public required string FormRevision { get; init; }

    public int Version { get; init; } = 1;

    public DateTimeOffset EffectiveFrom { get; init; }

    /// <summary>e.g. "EXP-{yyyy}-{seq:000000}".</summary>
    public required string NumberFormat { get; init; }

    public string WorkingCalendar { get; init; } = "NG_STANDARD";

    public bool SlaPausesOutsideWorkingHours { get; init; } = true;

    public IReadOnlyList<PolicyValue> PolicyValues { get; init; } = Array.Empty<PolicyValue>();

    public required IReadOnlyList<WorkflowState> States { get; init; }

    public required IReadOnlyList<WorkflowTransition> Transitions { get; init; }

    public IReadOnlyList<NotificationRule> Notifications { get; init; } =
        Array.Empty<NotificationRule>();

    public WorkflowState? FindState(string key) =>
        States.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    public WorkflowState InitialState =>
        States.FirstOrDefault(s => s.Type == StateType.Initial)
        ?? throw new InvalidOperationException(
            $"Workflow '{ModuleKey}' has no initial state.");

    public IEnumerable<WorkflowTransition> TransitionsFrom(string stateKey) =>
        Transitions.Where(t => string.Equals(t.From, stateKey, StringComparison.Ordinal));
}

public sealed class PolicyValue
{
    public required string Key { get; init; }
    public required decimal Value { get; init; }

    /// <summary>When this figure took effect. Lets a single definition
    /// version carry more than one dated row for the same key (e.g. a
    /// threshold that changes mid-year without a module version bump) --
    /// GetPolicyValue picks the latest row whose EffectiveFrom has passed as
    /// of the lookup date. Defaults to "always effective" for policy values
    /// that only ever need one row.</summary>
    public DateTimeOffset EffectiveFrom { get; init; } = DateTimeOffset.MinValue;

    public string? Note { get; init; }
}

public static class WorkflowDefinitionExtensions
{
    /// <summary>
    /// Reads a named figure out of the definition's own policy table rather
    /// than a C# constant, so a policy change ships as a JSON edit -- and,
    /// via EffectiveFrom, as a dated JSON edit: adding a new row with a
    /// future EffectiveFrom lands the change on that date without touching
    /// requests already recalculating today. Among rows for this key whose
    /// EffectiveFrom is on or before <paramref name="asOf"/>, the one with
    /// the latest EffectiveFrom wins (see ExpenseRequest.ApplyPaymentMethodPolicy
    /// and CashAdvanceRequest.ApplyPaymentMethodPolicy, both called on every
    /// totals recalculation so a policy change takes effect on the next
    /// edit, not just for new requests).
    /// </summary>
    public static decimal GetPolicyValue(this WorkflowDefinition definition, string key, DateTimeOffset asOf)
    {
        var candidate = definition.PolicyValues
            .Where(p => string.Equals(p.Key, key, StringComparison.Ordinal) && p.EffectiveFrom <= asOf)
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();

        return candidate?.Value ?? throw new InvalidOperationException(
            $"Module '{definition.ModuleKey}' has no policy value named '{key}' effective as of {asOf:O}.");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StateType
{
    /// <summary>Ordinary state awaiting an actor.</summary>
    Normal,

    /// <summary>Entry point. Exactly one per definition.</summary>
    Initial,

    /// <summary>End of life. No outbound transitions.</summary>
    Terminal,

    /// <summary>Returned to the requester for correction. Not a rejection --
    /// the request keeps its number and history and re-enters the flow.</summary>
    Rework
}

public sealed class WorkflowState
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public StateType Type { get; init; } = StateType.Normal;

    public SlaPolicy? Sla { get; init; }
}

public sealed class SlaPolicy
{
    /// <summary>Hours allowed in this state before breach.</summary>
    public required int Hours { get; init; }

    /// <summary>Reminder cadence. Null means no reminders.</summary>
    public int? ReminderEveryHours { get; init; }

    /// <summary>State key to escalate to on breach. Escalation transfers
    /// authority to act, it does not merely notify -- otherwise the SLA is
    /// advisory and delay stays hidden.</summary>
    public string? EscalateTo { get; init; }
}

public sealed class WorkflowTransition
{
    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>SUBMIT, VERIFY, APPROVE, RETURN, REJECT, POST, AUTHORISE,
    /// EXECUTE_PAYMENT, ACKNOWLEDGE, CONFIRM_REFUND, RESUBMIT.</summary>
    public required string Action { get; init; }

    public required ActorSpec Actor { get; init; }

    /// <summary>Expression in the restricted guard language. Null means no
    /// condition beyond actor authority.</summary>
    public string? Guard { get; init; }

    /// <summary>Message shown to the user when the guard fails. Without this a
    /// blocked approver sees only that the button did nothing.</summary>
    public string? GuardMessage { get; init; }

    public bool RequiresComment { get; init; }

    /// <summary>Fields the actor must supply as part of this transition,
    /// e.g. TreasuryNumber at Finance verification.</summary>
    public IReadOnlyList<string> Captures { get; init; } = Array.Empty<string>();

    /// <summary>Field on the request stamped with the acting user's id,
    /// e.g. PostedByUserId, so a later maker-checker guard can compare it.</summary>
    public string? Records { get; init; }

    /// <summary>Named side effect, e.g. IncrementRevisionNumber.</summary>
    public string? Effect { get; init; }

    public string? Note { get; init; }
}

public sealed class ActorSpec
{
    /// <summary>Static role requirement, e.g. FinanceManager.</summary>
    public string? Role { get; init; }

    /// <summary>Dynamic resolver, e.g. LineManagerOf / DepartmentHeadOf /
    /// Requester / Beneficiary. Definitions never name an individual.</summary>
    public string? Resolver { get; init; }

    public string? Arg { get; init; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Role) || !string.IsNullOrWhiteSpace(Resolver);
}

public sealed class NotificationRule
{
    public required string On { get; init; }

    [JsonConverter(typeof(StringOrArrayConverter))]
    public IReadOnlyList<string> To { get; init; } = Array.Empty<string>();

    public required string Template { get; init; }
}
