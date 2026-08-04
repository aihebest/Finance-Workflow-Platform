using System.Text.Json;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Workflow;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Hourly sweep that reminds the current actor of work sitting in a state
/// with a reminder cadence, before the SLA is breached.
///
/// CADENCE WITHOUT A "LAST REMINDED" COLUMN
/// ----------------------------------------
/// Request already carries StateEnteredAt and ReminderCount, so the next
/// reminder is due at StateEnteredAt + (ReminderCount + 1) x
/// reminderEveryHours. Deriving it this way rather than storing a timestamp
/// has a useful property: the schedule is anchored to when the work arrived,
/// so a sweep that misses a tick (deployment, outage, lock contention) does
/// not push every subsequent reminder later. It catches up instead.
///
/// ReminderCount is reset on every state change, so an approver who receives
/// an escalated item starts a fresh cadence rather than inheriting a count
/// that has already expired.
///
/// A request past its SLA is left alone: EscalationSweep owns it from that
/// point, and reminding someone about work that has already been taken off
/// them is noise that trains people to ignore the mail.
///
/// Reminders are counted in CALENDAR hours deliberately, unlike the SLA
/// deadline itself. reminderEveryHours is a nudge cadence, not an
/// entitlement, and a working-hours cadence would go silent over a weekend
/// exactly when a Friday submission is most at risk of being forgotten.
/// </summary>
internal sealed partial class ReminderSweep
{
    private const string LockResource = "Desicon.Workflow.ReminderSweep";

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly IWorkflowDefinitionProvider _definitions;
    private readonly ILogger<ReminderSweep> _logger;

    public ReminderSweep(
        WorkflowDbContext db,
        IWorkflowClock clock,
        IWorkflowDefinitionProvider definitions,
        ILogger<ReminderSweep> logger)
    {
        _db = db;
        _clock = clock;
        _definitions = definitions;
        _logger = logger;
    }

    [Function(nameof(ReminderSweep))]
    public async Task RunAsync(
        [TimerTrigger("0 45 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        await using var applicationLock = await SqlApplicationLock.TryAcquireAsync(
            _db, LockResource, TimeSpan.Zero, cancellationToken);

        if (!applicationLock.Acquired)
        {
            LogSkipped(now, LockResource);
            return;
        }

        // Open, not yet breached, and actually assigned to someone. An
        // unassigned request has nobody to remind; it surfaces through
        // role-based inbox queries instead.
        var candidates = await _db.Requests
            .Where(r => r.ClosedAt == null
                        && r.CurrentActorId != null
                        && (r.SlaDueAt == null || r.SlaDueAt >= now))
            .OrderBy(r => r.StateEnteredAt)
            .ToListAsync(cancellationToken);

        LogEvaluating(candidates.Count, now);

        var reminded = 0;

        foreach (var request in candidates)
        {
            var definition = await _definitions.GetAsync(request.ModuleKey, cancellationToken);
            var state = definition.FindState(request.CurrentState);

            if (state?.Sla?.ReminderEveryHours is not { } everyHours || everyHours <= 0)
            {
                continue;
            }

            var nextDueAt = request.StateEnteredAt.AddHours((double)everyHours * (request.ReminderCount + 1));

            if (now < nextDueAt)
            {
                continue;
            }

            request.ReminderCount++;

            AppendReminderNotifications(definition, request, now);

            reminded++;

            LogReminded(request.RequestNumber, request.CurrentState, request.CurrentActorId, request.ReminderCount);
        }

        await _db.SaveChangesAsync(cancellationToken);

        LogComplete(reminded, candidates.Count);
    }

    private void AppendReminderNotifications(
        Core.Definitions.WorkflowDefinition definition,
        Domain.Requests.Request request,
        DateTimeOffset now)
    {
        foreach (var rule in definition.Notifications)
        {
            if (!string.Equals(rule.On, "SLA_REMINDER", StringComparison.Ordinal))
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
                    request.CurrentState,
                    request.SlaDueAt,
                    request.ReminderCount
                }),
                Status = OutboxMessageStatus.Pending,
                CreatedAt = now
            });
        }
    }

    [LoggerMessage(
        EventId = 6201,
        Level = LogLevel.Information,
        Message = "ReminderSweep skipped at {Now}: another instance holds {LockResource}.")]
    private partial void LogSkipped(DateTimeOffset now, string lockResource);

    [LoggerMessage(
        EventId = 6202,
        Level = LogLevel.Information,
        Message = "ReminderSweep evaluating {Count} open request(s) at {Now}.")]
    private partial void LogEvaluating(int count, DateTimeOffset now);

    [LoggerMessage(
        EventId = 6203,
        Level = LogLevel.Information,
        Message = "Reminded {ActorId} about {RequestNumber} in {CurrentState} (reminder {ReminderCount}).")]
    private partial void LogReminded(string requestNumber, string currentState, Guid? actorId, int reminderCount);

    [LoggerMessage(
        EventId = 6204,
        Level = LogLevel.Information,
        Message = "ReminderSweep complete: {Reminded} reminder(s) queued of {Count} evaluated.")]
    private partial void LogComplete(int reminded, int count);
}
