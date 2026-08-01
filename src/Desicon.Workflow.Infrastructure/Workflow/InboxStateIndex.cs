using Desicon.Workflow.Core.Definitions;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Which states, per module and role, currently sit in that role's queue --
/// built once from the published (data, not code) workflow definitions, the
/// same way WorkflowDefinitionValidator treats them as immutable for the
/// process lifetime.
///
/// Exists to make GET /my/inbox a single indexed query rather than a guard
/// evaluation over every open request: a transition whose actor is a bare
/// role (no resolver, e.g. FinanceOfficer) never collapses to one person, so
/// unlike a resolver-based transition it cannot be captured by
/// Request.CurrentActorId (see RequestActionService). Membership of a role's
/// queue is instead "the request's CurrentState has an outbound transition
/// gated on that role" -- computable once, ahead of time, from the
/// definitions alone.
/// </summary>
public sealed class InboxStateIndex
{
    private readonly Dictionary<(string ModuleKey, string Role), HashSet<string>> _statesByModuleAndRole;

    public InboxStateIndex(IEnumerable<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _statesByModuleAndRole = new Dictionary<(string, string), HashSet<string>>();

        foreach (var definition in definitions)
        {
            foreach (var transition in definition.Transitions)
            {
                if (transition.Actor.Resolver is not null || transition.Actor.Role is not { } role)
                {
                    continue;
                }

                var key = (definition.ModuleKey, role);
                if (!_statesByModuleAndRole.TryGetValue(key, out var states))
                {
                    states = new HashSet<string>(StringComparer.Ordinal);
                    _statesByModuleAndRole[key] = states;
                }

                states.Add(transition.From);
            }
        }
    }

    /// <summary>Every (module, state) pair where one of the supplied roles
    /// currently holds the queue.</summary>
    public IReadOnlyCollection<(string ModuleKey, string State)> StatesFor(IEnumerable<string> roles)
    {
        var result = new HashSet<(string, string)>();

        foreach (var role in roles)
        {
            foreach (var ((moduleKey, candidateRole), states) in _statesByModuleAndRole)
            {
                if (!string.Equals(candidateRole, role, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var state in states)
                {
                    result.Add((moduleKey, state));
                }
            }
        }

        return result;
    }
}
