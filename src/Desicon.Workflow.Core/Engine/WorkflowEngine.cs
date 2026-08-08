using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Guards;

namespace Desicon.Workflow.Core.Engine;

/// <summary>
/// The request as the engine sees it. The engine knows nothing about expenses,
/// advances or requisitions -- only that a thing has a state, some fields, and
/// a requester. That is what makes a new module a data change rather than a
/// code change.
/// </summary>
public interface IWorkflowSubject : IGuardFieldSource
{
    Guid RequestId { get; }
    string ModuleKey { get; }
    string CurrentState { get; }
    Guid RequesterId { get; }
    int DepartmentId { get; }
}

public interface IActorResolver
{
    /// <summary>Resolves the set of user ids permitted to act under this spec.</summary>
    Task<IReadOnlySet<Guid>> ResolveAsync(
        ActorSpec spec, IWorkflowSubject subject, CancellationToken cancellationToken = default);
}

public sealed record ActingUser(
    Guid UserId,
    IReadOnlySet<string> Roles,
    Guid? OnBehalfOf = null);

public sealed record TransitionRequest(
    string Action,
    string? Comment = null,
    IReadOnlyDictionary<string, object?>? CapturedFields = null,
    string? IdempotencyKey = null);

public enum TransitionOutcome
{
    Succeeded,
    NoSuchTransition,
    NotAuthorised,
    GuardFailed,
    CommentRequired,
    MissingCapturedField,

    /// <summary>
    /// Never produced by <see cref="WorkflowEngine"/> itself -- the engine has
    /// already returned <see cref="Succeeded"/> by the time this applies. It is
    /// set by a caller (RequestActionService) after an independent,
    /// defence-in-depth check -- self-approval or maker-checker -- rejects a
    /// transition the definition's own guards allowed. Kept in this enum
    /// because it is the same vocabulary of "why didn't this go through" as
    /// the engine's own denial outcomes.
    /// </summary>
    PolicyViolation
}

public sealed record TransitionResult(
    TransitionOutcome Outcome,
    string? FromState = null,
    string? ToState = null,
    string? Message = null,
    WorkflowTransition? Transition = null)
{
    public bool Succeeded => Outcome == TransitionOutcome.Succeeded;

    public static TransitionResult Fail(TransitionOutcome outcome, string message) =>
        new(outcome, Message: message);
}

/// <summary>
/// Executes workflow transitions. Every state change in the platform goes
/// through <see cref="ExecuteAsync"/> -- there is no second path. That single
/// choice is what guarantees an audit event is written for every transition,
/// because there is nowhere else a transition can happen.
/// </summary>
/// <summary>
/// A transition the acting user is authorised for, and whether its guard
/// currently lets them take it.
/// </summary>
/// <param name="GuardSatisfied">False means the action is theirs to take but
/// something about the request is not yet in order -- not that they lack the
/// authority.</param>
/// <param name="BlockedReason">The definition's guardMessage. Written for a
/// person, and names what to fix.</param>
public sealed record TransitionAvailability(
    WorkflowTransition Transition,
    bool GuardSatisfied,
    string? BlockedReason);

public sealed class WorkflowEngine
{
    private readonly IActorResolver _actorResolver;
    private readonly GuardEvaluator _guardEvaluator;
    private readonly IWorkflowClock _clock;

    public WorkflowEngine(
        IActorResolver actorResolver,
        GuardEvaluator guardEvaluator,
        IWorkflowClock clock)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _guardEvaluator = guardEvaluator ?? throw new ArgumentNullException(nameof(guardEvaluator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Returns the transitions this user could execute right now. Used to drive
    /// the UI's action buttons -- but the UI's opinion is cosmetic. The same
    /// checks run again inside ExecuteAsync.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowTransition>> GetAvailableTransitionsAsync(
        WorkflowDefinition definition,
        IWorkflowSubject subject,
        ActingUser user,
        CancellationToken cancellationToken = default)
    {
        var availability = await GetTransitionAvailabilityAsync(definition, subject, user, cancellationToken);

        return availability
            .Where(a => a.GuardSatisfied)
            .Select(a => a.Transition)
            .ToList();
    }

    /// <summary>
    /// Every transition this user is *authorised* for, each carrying whether
    /// its guard currently passes and, if not, why.
    /// </summary>
    /// <remarks>
    /// Authorisation and guard satisfaction are separated because a screen
    /// needs them separately, and collapsing them produced a deadlock that
    /// reached dev.
    ///
    /// GetAvailableTransitionsAsync drops any transition whose guard fails.
    /// Several guards exist precisely to require data the user has not
    /// supplied yet -- FINANCE_VERIFY's VERIFY needs a Treasury number, POST
    /// needs balanced GL lines, CONFIRM_REFUND needs the refund amount. Gate
    /// the capture UI on the action being available and the value can never be
    /// entered, because the field that supplies it renders only once the value
    /// is already there. Three of this platform's capture steps were
    /// unreachable in the browser for exactly that reason.
    ///
    /// So: authorised transitions are always returned. GuardSatisfied says
    /// whether the button is live, and BlockedReason carries the definition's
    /// own guardMessage, which is written for a person and names the condition
    /// to fix. Showing a disabled action with that sentence beside it is more
    /// use than showing nothing, which is indistinguishable from having no
    /// authority at all.
    /// </remarks>
    public async Task<IReadOnlyList<TransitionAvailability>> GetTransitionAvailabilityAsync(
        WorkflowDefinition definition,
        IWorkflowSubject subject,
        ActingUser user,
        CancellationToken cancellationToken = default)
    {
        var availability = new List<TransitionAvailability>();

        foreach (var transition in definition.TransitionsFrom(subject.CurrentState))
        {
            if (!await IsAuthorisedAsync(transition, subject, user, cancellationToken))
            {
                continue;
            }

            var satisfied = TryEvaluateGuard(transition, subject, out var message);

            availability.Add(new TransitionAvailability(
                transition, satisfied, satisfied ? null : message));
        }

        return availability;
    }

    public async Task<TransitionResult> ExecuteAsync(
        WorkflowDefinition definition,
        IWorkflowSubject subject,
        ActingUser user,
        TransitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        var candidates = definition
            .TransitionsFrom(subject.CurrentState)
            .Where(t => string.Equals(t.Action, request.Action, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return TransitionResult.Fail(
                TransitionOutcome.NoSuchTransition,
                $"Action '{request.Action}' is not available from state '{subject.CurrentState}'.");
        }

        // Layer 1 and 2: role, then actor identity for this specific request.
        var authorised = new List<WorkflowTransition>();

        foreach (var candidate in candidates)
        {
            if (await IsAuthorisedAsync(candidate, subject, user, cancellationToken))
            {
                authorised.Add(candidate);
            }
        }

        if (authorised.Count == 0)
        {
            return TransitionResult.Fail(
                TransitionOutcome.NotAuthorised,
                $"You are not the current approver for this request.");
        }

        // Layer 3: guards. Several transitions can share an action and be
        // separated only by their guard -- the expense module's APPROVE splits
        // to POSTING or REFUND_DUE on the sign of NetPayableNgn exactly this way.
        WorkflowTransition? selected = null;
        string? lastGuardMessage = null;

        foreach (var candidate in authorised)
        {
            if (TryEvaluateGuard(candidate, subject, out var guardMessage))
            {
                selected = candidate;
                break;
            }

            lastGuardMessage = guardMessage;
        }

        if (selected is null)
        {
            return TransitionResult.Fail(
                TransitionOutcome.GuardFailed,
                lastGuardMessage ?? "This action is not currently permitted on this request.");
        }

        if (selected.RequiresComment && string.IsNullOrWhiteSpace(request.Comment))
        {
            return TransitionResult.Fail(
                TransitionOutcome.CommentRequired,
                "A comment is required for this action.");
        }

        foreach (var field in selected.Captures)
        {
            if (request.CapturedFields is null ||
                !request.CapturedFields.TryGetValue(field, out var value) ||
                value is null ||
                (value is string s && string.IsNullOrWhiteSpace(s)))
            {
                return TransitionResult.Fail(
                    TransitionOutcome.MissingCapturedField,
                    $"'{field}' must be supplied to complete this action.");
            }
        }

        return new TransitionResult(
            TransitionOutcome.Succeeded,
            FromState: subject.CurrentState,
            ToState: selected.To,
            Transition: selected);
    }

    /// <summary>
    /// Computes the SLA deadline for a newly entered state. Returns null where
    /// the state has no SLA.
    /// </summary>
    public DateTimeOffset? ComputeSlaDueAt(WorkflowDefinition definition, string stateKey)
    {
        var state = definition.FindState(stateKey);

        if (state?.Sla is null)
        {
            return null;
        }

        var now = _clock.UtcNow;

        return definition.SlaPausesOutsideWorkingHours
            ? _clock.AddWorkingHours(now, state.Sla.Hours, definition.WorkingCalendar)
            : now.AddHours(state.Sla.Hours);
    }

    private async Task<bool> IsAuthorisedAsync(
        WorkflowTransition transition,
        IWorkflowSubject subject,
        ActingUser user,
        CancellationToken cancellationToken)
    {
        var spec = transition.Actor;

        if (spec.Role is { } requiredRole && !user.Roles.Contains(requiredRole))
        {
            return false;
        }

        // A role alone never grants action on a specific request. Holding
        // LineManager does not let you act on another manager's queue.
        var permitted = await _actorResolver.ResolveAsync(spec, subject, cancellationToken);

        if (permitted.Count == 0)
        {
            // Role-only specs resolve to the whole role membership; an empty
            // set means no one is eligible, which is a configuration fault
            // rather than a silent denial.
            return spec.Resolver is null && spec.Role is not null;
        }

        var effectiveUserId = user.OnBehalfOf ?? user.UserId;
        return permitted.Contains(effectiveUserId);
    }

    private bool TryEvaluateGuard(
        WorkflowTransition transition, IWorkflowSubject subject, out string? message)
    {
        message = null;

        if (transition.Guard is null)
        {
            return true;
        }

        try
        {
            var ast = GuardCache.GetOrParse(transition.Guard);

            if (_guardEvaluator.EvaluateBoolean(ast, subject))
            {
                return true;
            }

            message = transition.GuardMessage
                      ?? $"Condition not met: {transition.Guard}";
            return false;
        }
        catch (GuardEvaluationException ex)
        {
            // A guard that cannot be evaluated must block, never wave through.
            message = $"This action could not be evaluated: {ex.Message}";
            return false;
        }
    }
}

public interface IWorkflowClock
{
    DateTimeOffset UtcNow { get; }

    DateTimeOffset AddWorkingHours(DateTimeOffset from, int hours, string calendarKey);
}

internal static class GuardCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GuardNode>
        Cache = new(StringComparer.Ordinal);

    public static GuardNode GetOrParse(string expression) =>
        Cache.GetOrAdd(expression, GuardParser.Parse);
}
