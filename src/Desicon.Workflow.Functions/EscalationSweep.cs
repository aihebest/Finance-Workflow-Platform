using System.Text.Json;
using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Hourly sweep that escalates requests whose SLA has been breached.
///
/// WHAT ESCALATION MEANS HERE
/// --------------------------
/// It transfers authority. `sla.escalateTo` names a *state*, not a person,
/// and moving the request into that state makes its actor the one who can
/// now act — because authorisation is derived from the current state, not
/// from a field naming an approver. A notification-only escalation would
/// leave the original approver as the only person able to proceed, which
/// makes the SLA advisory and leaves the delay exactly where it was. That is
/// the failure this whole step exists to prevent.
///
/// The audit event names the person who did not act. Not to punish anyone —
/// the point of a chased request is that someone can answer "who was it
/// waiting on", and after escalation CurrentActorId no longer says.
///
/// WHY THIS DOES NOT GO THROUGH RequestActionService
/// -------------------------------------------------
/// That service executes *declared transitions*: it looks one up by action
/// name, evaluates its guards, and checks the caller is an authorised actor.
/// Escalation is none of those things. It is not in the transitions list, no
/// human performs it, and it must succeed precisely when the authorised
/// actor has failed to do anything — so guard evaluation and actor
/// authorisation are not merely unnecessary, they would block it.
///
/// The cost is that this code must maintain the invariants that service
/// normally maintains: current actor, SLA deadline, state-entry timestamp,
/// audit chain, outbox. They are set explicitly below rather than inherited,
/// and that is a real duplication risk worth naming — if a future field joins
/// the "must be updated on state change" set, it has to be added here too.
/// </summary>
internal sealed partial class EscalationSweep
{
    private const string LockResource = "Desicon.Workflow.EscalationSweep";

    /// <summary>
    /// Actor recorded on escalation audit events. Escalation has no human
    /// actor by definition — attributing it to the person who failed to act
    /// would misread as them having done something.
    /// </summary>
    private static readonly Guid SystemActorId = new("00000000-0000-0000-0000-00000000E5CA");

    private const string SystemActorRole = "SYSTEM_ESCALATION_SWEEP";

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly IWorkflowDefinitionProvider _definitions;
    private readonly IActorResolver _actorResolver;
    private readonly WorkflowEngine _engine;
    private readonly ILogger<EscalationSweep> _logger;

    public EscalationSweep(
        WorkflowDbContext db,
        IWorkflowClock clock,
        IWorkflowDefinitionProvider definitions,
        IActorResolver actorResolver,
        WorkflowEngine engine,
        ILogger<EscalationSweep> logger)
    {
        _db = db;
        _clock = clock;
        _definitions = definitions;
        _actorResolver = actorResolver;
        _engine = engine;
        _logger = logger;
    }

    [Function(nameof(EscalationSweep))]
    public async Task RunAsync(
        [TimerTrigger("0 15 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        await using var applicationLock = await SqlApplicationLock.TryAcquireAsync(
            _db, LockResource, TimeSpan.Zero, cancellationToken);

        if (!applicationLock.Acquired)
        {
            // Expected on a scaled-out plan. Duplicate escalation is not a
            // duplicate email: it is two transfers of authority for one
            // request and two audit entries naming a non-actor, neither of
            // which can be undone by a retry.
            LogSkipped(now, LockResource);
            return;
        }

        var breached = await _db.Requests
            .Where(r => r.ClosedAt == null
                        && r.SlaDueAt != null
                        && r.SlaDueAt < now)
            .OrderBy(r => r.SlaDueAt)
            .ToListAsync(cancellationToken);

        LogEvaluating(breached.Count, now);

        var escalated = 0;

        foreach (var request in breached)
        {
            var definition = await _definitions.GetAsync(
                request.ModuleKey, request.DefinitionVersion, cancellationToken);
            var state = definition.FindState(request.CurrentState);

            if (state?.Sla?.EscalateTo is not { } target)
            {
                // Breached with nowhere to escalate to. The deadline has
                // still passed and that is worth saying once, but there is no
                // authority to transfer, so this is a notification not a
                // state change.
                LogBreachWithoutTarget(request.RequestNumber, request.CurrentState);
                continue;
            }

            var failedActorId = request.CurrentActorId;
            var fromState = request.CurrentState;

            request.CurrentState = target;
            request.StateEnteredAt = now;
            request.EscalationCount++;

            // Reset so the escalated approver gets their own reminder cadence
            // rather than inheriting a count that already expired.
            request.ReminderCount = 0;

            request.CurrentActorId = await ResolveActorAsync(request, definition, cancellationToken);
            request.SlaDueAt = _engine.ComputeSlaDueAt(definition, target);

            await AppendEscalationAuditEventAsync(
                request, fromState, target, failedActorId, now, cancellationToken);

            AppendBreachNotifications(definition, request, fromState, target, now);

            escalated++;

            LogEscalated(request.RequestNumber, fromState, target, failedActorId, request.EscalationCount);
        }

        await _db.SaveChangesAsync(cancellationToken);

        LogComplete(escalated, breached.Count);
    }

    /// <summary>
    /// Resolves who may now act, using the same union-of-transition-actors
    /// rule RequestActionService applies after any state change: a single
    /// unambiguous candidate becomes CurrentActorId, anything else leaves it
    /// null and the item surfaces through role-based inbox queries instead.
    /// </summary>
    private async Task<Guid?> ResolveActorAsync(
        Request request, WorkflowDefinition definition, CancellationToken cancellationToken)
    {
        var candidates = new HashSet<Guid>();

        foreach (var transition in definition.TransitionsFrom(request.CurrentState))
        {
            if (transition.Actor.Resolver is null)
            {
                return null;
            }

            candidates.UnionWith(await _actorResolver.ResolveAsync(transition.Actor, request, cancellationToken));

            if (candidates.Count > 1)
            {
                return null;
            }
        }

        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private async Task AppendEscalationAuditEventAsync(
        Request request,
        string fromState,
        string toState,
        Guid? failedActorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previousHash = await _db.AuditEvents
            .Where(e => e.RequestId == request.RequestId)
            .OrderByDescending(e => e.AuditEventId)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        var auditEvent = new AuditEvent
        {
            RequestId = request.RequestId,
            EventType = "ESCALATED",
            FromState = fromState,
            ToState = toState,
            ActorId = SystemActorId,
            ActorRole = SystemActorRole,

            // The person who did not act, recorded where a human actor would
            // normally sit. After escalation CurrentActorId names the new
            // approver, so without this the question "who was this waiting
            // on" has no answer anywhere in the record.
            OnBehalfOfUserId = failedActorId,

            Reason = failedActorId is null
                ? $"SLA breached in {fromState}; no actor was assigned."
                : $"SLA breached in {fromState}; {failedActorId} did not act.",

            PayloadJson = JsonSerializer.Serialize(new
            {
                SlaDueAt = request.SlaDueAt,
                request.EscalationCount,
                FailedActorId = failedActorId
            }),

            OccurredAtUtc = now,

            // Keyed on the escalation count so a repeat breach in the same
            // state escalates again, but one sweep pass cannot double-write.
            IdempotencyKey = $"ESCALATED:{request.RequestId}:{request.EscalationCount}"
        };

        auditEvent.Seal(previousHash);

        _db.AuditEvents.Add(auditEvent);
    }

    /// <summary>
    /// Emits the SLA_BREACHED notifications the module declares.
    /// RequestActionService deliberately does not match these rules — see the
    /// comment on its BuildOutboxMessages, which names this sweep as their
    /// owner.
    /// </summary>
    private void AppendBreachNotifications(
        WorkflowDefinition definition,
        Request request,
        string fromState,
        string toState,
        DateTimeOffset now)
    {
        foreach (var rule in definition.Notifications)
        {
            if (!string.Equals(rule.On, "SLA_BREACHED", StringComparison.Ordinal))
            {
                continue;
            }

            _db.OutboxMessages.Add(new OutboxMessage
            {
                RequestId = request.RequestId,
                Template = rule.Template,
                RecipientRolesJson = JsonSerializer.Serialize(rule.To),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    request.RequestId,
                    request.RequestNumber,
                    request.ModuleKey,
                    FromState = fromState,
                    ToState = toState,
                    request.EscalationCount
                }),
                Status = OutboxMessageStatus.Pending,
                CreatedAt = now
            });
        }
    }

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Information,
        Message = "EscalationSweep skipped at {Now}: another instance holds {LockResource}.")]
    private partial void LogSkipped(DateTimeOffset now, string lockResource);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Information,
        Message = "EscalationSweep evaluating {Count} breached request(s) at {Now}.")]
    private partial void LogEvaluating(int count, DateTimeOffset now);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Warning,
        Message = "Escalated {RequestNumber} from {FromState} to {ToState}; {FailedActorId} did not act (escalation {EscalationCount}).")]
    private partial void LogEscalated(
        string requestNumber, string fromState, string toState, Guid? failedActorId, int escalationCount);

    [LoggerMessage(
        EventId = 6104,
        Level = LogLevel.Warning,
        Message = "{RequestNumber} breached its SLA in {CurrentState}, which declares no escalation target.")]
    private partial void LogBreachWithoutTarget(string requestNumber, string currentState);

    [LoggerMessage(
        EventId = 6105,
        Level = LogLevel.Information,
        Message = "EscalationSweep complete: {Escalated} escalated of {Count} breached.")]
    private partial void LogComplete(int escalated, int count);
}
