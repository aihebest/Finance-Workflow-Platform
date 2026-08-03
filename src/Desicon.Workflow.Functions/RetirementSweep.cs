using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Daily sweep that moves cash advances past their retirement due date into
/// Overdue, and records the transition in the audit chain.
///
/// WHAT THIS DOES NOT DO
/// ---------------------
/// It does not compute the due date. <c>CashAdvanceRequest.ReleaseCash</c>
/// already set <c>RetirementDueDate</c> from the working calendar at the
/// moment Finance released the cash, and that stored value is authoritative.
/// Recomputing it here would silently re-date every outstanding advance
/// whenever the holiday table changes — including advances already flagged
/// overdue, which is precisely the kind of retroactive movement an audit
/// trail is supposed to prevent.
///
/// The working calendar therefore governs this sweep through the due date it
/// produced earlier, not through arithmetic performed now. That is the
/// distinction the build plan warns about: "the retirement sweep must use the
/// working calendar, not AddHours". Doing the comparison against a stored
/// calendar-derived instant satisfies it; recomputing would break audit
/// stability while appearing more correct.
///
/// TIMING
/// ------
/// 06:00 West Africa Time, expressed as 05:00 UTC. Azure Functions evaluates
/// NCRONTAB against WEBSITE_TIME_ZONE, which is not set on Linux consumption
/// plans by default and would silently mean UTC — so the offset is applied
/// here rather than assumed. Nigeria is UTC+1 year round with no daylight
/// saving, so this does not drift.
/// </summary>
internal sealed partial class RetirementSweep
{
    /// <summary>
    /// Named lock resource. Shared by every instance of this function across
    /// the whole Function App, so a scaled-out plan runs the sweep once.
    /// </summary>
    private const string LockResource = "Desicon.Workflow.RetirementSweep";

    /// <summary>
    /// Actor recorded on audit events this sweep writes. A transition to
    /// Overdue has no human actor — nobody did anything, which is the point —
    /// but the audit chain requires an ActorId, and leaving it as the
    /// requester's id would misattribute an automated finding to the person
    /// it is about.
    /// </summary>
    private static readonly Guid SystemActorId = new("00000000-0000-0000-0000-00000000FEED");

    private const string SystemActorRole = "SYSTEM_RETIREMENT_SWEEP";

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly ILogger<RetirementSweep> _logger;

    public RetirementSweep(WorkflowDbContext db, IWorkflowClock clock, ILogger<RetirementSweep> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    [Function(nameof(RetirementSweep))]
    public async Task RunAsync(
        [TimerTrigger("0 0 5 * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        await using var applicationLock = await SqlApplicationLock.TryAcquireAsync(
            _db, LockResource, TimeSpan.Zero, cancellationToken);

        if (!applicationLock.Acquired)
        {
            // Expected on a scaled-out plan: another instance is already
            // sweeping. Information, not a warning — logging it as a problem
            // would train people to ignore this log.
            LogSkipped(now, LockResource);
            return;
        }

        var candidates = await LoadCandidatesAsync(now, cancellationToken);

        LogEvaluating(candidates.Count, now);

        var newlyOverdue = 0;

        foreach (var advance in candidates)
        {
            var previousStatus = advance.RetirementStatus;

            advance.RecalculateRetirementStatus(now);

            if (advance.RetirementStatus == previousStatus)
            {
                continue;
            }

            // Only the crossing into Overdue is auditable. Movement between
            // NotDue, Due and PartiallyRetired follows mechanically from data
            // recorded elsewhere, and writing an event for each would bury
            // the one that carries consequence.
            if (advance.RetirementStatus == RetirementStatus.Overdue)
            {
                await AppendOverdueAuditEventAsync(advance, previousStatus, now, cancellationToken);
                newlyOverdue++;

                LogOverdue(
                    advance.RequestNumber,
                    advance.RetirementDueDate,
                    advance.RetirementBalanceNgn,
                    advance.RequesterId);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        LogComplete(newlyOverdue, candidates.Count);
    }

    // Source-generated logging delegates. CA1848 requires these over the
    // ILogger extension methods: the extensions box every argument and format
    // the template on each call, including when the level is disabled. A
    // sweep that logs per advance does that on every row, every day.
    //
    // No format specifiers in the templates ({Now} rather than {Now:o}) --
    // the generator emits the value as-is and the sink decides rendering,
    // which is what keeps these structured rather than pre-flattened strings.

    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "RetirementSweep skipped at {Now}: another instance holds {LockResource}.")]
    private partial void LogSkipped(DateTimeOffset now, string lockResource);

    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Information,
        Message = "RetirementSweep evaluating {Count} outstanding advance(s) at {Now}.")]
    private partial void LogEvaluating(int count, DateTimeOffset now);

    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Warning,
        Message = "Advance {RequestNumber} is overdue: due {DueDate}, balance {Balance} NGN, requester {RequesterId}.")]
    private partial void LogOverdue(string requestNumber, DateTimeOffset? dueDate, decimal balance, Guid requesterId);

    [LoggerMessage(
        EventId = 6004,
        Level = LogLevel.Information,
        Message = "RetirementSweep complete: {NewlyOverdue} advance(s) newly overdue of {Count} evaluated.")]
    private partial void LogComplete(int newlyOverdue, int count);

    /// <summary>
    /// Advances with cash released and a retirement balance outstanding.
    ///
    /// FullyRetired is excluded in the query rather than skipped in the loop:
    /// this table grows without bound and a sweep that loads every advance
    /// ever raised will be fine in dev and slow in year three.
    /// </summary>
    private async Task<List<Domain.Requests.CashAdvanceRequest>> LoadCandidatesAsync(
        DateTimeOffset now, CancellationToken cancellationToken) =>
        await _db.CashAdvanceRequests
            .Where(a => a.CashReleasedAt != null
                        && a.RetirementStatus != RetirementStatus.FullyRetired)
            .OrderBy(a => a.RetirementDueDate)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Appends to the per-request hash chain. Reads the previous hash inside
    /// the same unit of work as the insert, matching
    /// RequestActionService.RunTransitionAsync — the chain is per RequestId,
    /// so two requests going overdue in the same sweep do not contend.
    /// </summary>
    private async Task AppendOverdueAuditEventAsync(
        Domain.Requests.CashAdvanceRequest advance,
        RetirementStatus previousStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var previousHash = await _db.AuditEvents
            .Where(e => e.RequestId == advance.RequestId)
            .OrderByDescending(e => e.AuditEventId)
            .Select(e => e.EventHash)
            .FirstOrDefaultAsync(cancellationToken);

        var auditEvent = new AuditEvent
        {
            RequestId = advance.RequestId,
            EventType = "RETIREMENT_OVERDUE",
            FromState = previousStatus.ToString(),
            ToState = RetirementStatus.Overdue.ToString(),
            ActorId = SystemActorId,
            ActorRole = SystemActorRole,
            Reason = $"Retirement due {advance.RetirementDueDate:o}; balance {advance.RetirementBalanceNgn} NGN outstanding.",
            OccurredAtUtc = now,

            // Idempotency key includes the due date, not the sweep date: a
            // sweep that runs twice on one day must not append twice, and an
            // advance whose due date is later revised is a genuinely
            // different finding.
            IdempotencyKey = $"RETIREMENT_OVERDUE:{advance.RequestId}:{advance.RetirementDueDate:O}"
        };

        auditEvent.Seal(previousHash);

        _db.AuditEvents.Add(auditEvent);
    }
}
