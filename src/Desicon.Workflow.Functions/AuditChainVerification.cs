using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Nightly verification of the append-only audit chain.
///
/// WHY THIS IS NOT OPTIONAL
/// ------------------------
/// Every audit event is sealed with a hash over its own content and the
/// previous event's hash, which makes the chain tamper-evident. Evident to
/// whom, though? Nothing reads those hashes back. Until something does, the
/// chain is a claim about integrity rather than a check on it, and a row
/// edited directly in the database — the exact scenario the design exists to
/// catch — would go unnoticed indefinitely.
///
/// That is the same shape as the other controls found switched off in this
/// repo: a conftest gate evaluating zero rules, an action-pinning script
/// nothing ran, a guard-field schema nothing compared against. This function
/// is the reader that makes the writer meaningful.
///
/// WHAT A FAILURE MEANS
/// --------------------
/// Either someone modified or deleted an audit row outside the application,
/// or the hashing changed without a re-seal. Neither is recoverable by
/// retrying, and both are incidents rather than errors, so this logs at
/// Critical and then throws: a silent nightly function reporting success is
/// how the problem stays invisible, and a failed function invocation is what
/// raises an alert.
///
/// SCALE
/// -----
/// A full scan is correct and affordable while the table is small. It will
/// not stay small. The natural next step is to verify only chains with
/// events since the last successful run, and to record the last verified
/// AuditEventId per request — deliberately not done yet, because an
/// incremental checker that skips the rows an attacker edited is worse than
/// no checker at all, and getting that boundary right deserves its own
/// thought rather than being assumed here.
/// </summary>
internal sealed partial class AuditChainVerification
{
    private const string LockResource = "Desicon.Workflow.AuditChainVerification";

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly ILogger<AuditChainVerification> _logger;

    public AuditChainVerification(
        WorkflowDbContext db, IWorkflowClock clock, ILogger<AuditChainVerification> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    [Function(nameof(AuditChainVerification))]
    public async Task RunAsync(
        [TimerTrigger("0 30 2 * * *")] TimerInfo timer,
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

        // AsNoTracking: this reads a lot of rows and changes none of them.
        // Tracking them would hold the entire audit table in the change
        // tracker for the life of the sweep.
        var requestIds = await _db.AuditEvents
            .AsNoTracking()
            .Select(e => e.RequestId)
            .Distinct()
            .ToListAsync(cancellationToken);

        LogStarted(requestIds.Count, now);

        var broken = new List<string>();
        var eventsChecked = 0;

        foreach (var requestId in requestIds)
        {
            // Ordered by AuditEventId, the identity column, which is the
            // order they were appended in. Ordering by OccurredAtUtc would be
            // wrong: two events can share a timestamp, and a backdated row is
            // exactly what this is looking for.
            var chain = await _db.AuditEvents
                .AsNoTracking()
                .Where(e => e.RequestId == requestId)
                .OrderBy(e => e.AuditEventId)
                .ToListAsync(cancellationToken);

            byte[]? previousHash = null;

            foreach (var auditEvent in chain)
            {
                eventsChecked++;

                // Two distinct checks. The first catches a re-sealed event
                // spliced into the chain: its own hash is internally
                // consistent, but the link it claims does not match what
                // actually precedes it. The second catches an edited event:
                // the link is intact but the content no longer hashes to the
                // recorded value.
                var linkIntact = (previousHash is null && auditEvent.PreviousHash is null)
                    || (previousHash is not null
                        && auditEvent.PreviousHash is not null
                        && previousHash.SequenceEqual(auditEvent.PreviousHash));

                if (!linkIntact)
                {
                    broken.Add($"{requestId}:{auditEvent.AuditEventId} (previous-hash link)");
                    LogBrokenLink(requestId, auditEvent.AuditEventId, auditEvent.EventType);
                }
                else if (!auditEvent.VerifyAgainst(previousHash))
                {
                    broken.Add($"{requestId}:{auditEvent.AuditEventId} (content hash)");
                    LogBrokenContent(requestId, auditEvent.AuditEventId, auditEvent.EventType);
                }

                previousHash = auditEvent.EventHash;
            }
        }

        if (broken.Count > 0)
        {
            LogFailed(broken.Count, eventsChecked, string.Join(", ", broken.Take(20)));

            throw new InvalidOperationException(
                $"Audit chain verification failed for {broken.Count} event(s) across {requestIds.Count} request(s). " +
                "An audit row has been modified or removed outside the application. " +
                $"First failures: {string.Join(", ", broken.Take(20))}");
        }

        LogPassed(eventsChecked, requestIds.Count);
    }

    [LoggerMessage(
        EventId = 6301,
        Level = LogLevel.Information,
        Message = "AuditChainVerification skipped at {Now}: another instance holds {LockResource}.")]
    private partial void LogSkipped(DateTimeOffset now, string lockResource);

    [LoggerMessage(
        EventId = 6302,
        Level = LogLevel.Information,
        Message = "AuditChainVerification started at {Now} over {RequestCount} request chain(s).")]
    private partial void LogStarted(int requestCount, DateTimeOffset now);

    [LoggerMessage(
        EventId = 6303,
        Level = LogLevel.Critical,
        Message = "Audit chain broken for request {RequestId} at event {AuditEventId} ({EventType}): previous-hash link does not match the preceding event.")]
    private partial void LogBrokenLink(Guid requestId, long auditEventId, string eventType);

    [LoggerMessage(
        EventId = 6304,
        Level = LogLevel.Critical,
        Message = "Audit chain broken for request {RequestId} at event {AuditEventId} ({EventType}): content does not hash to the recorded value.")]
    private partial void LogBrokenContent(Guid requestId, long auditEventId, string eventType);

    [LoggerMessage(
        EventId = 6305,
        Level = LogLevel.Critical,
        Message = "AuditChainVerification FAILED: {BrokenCount} broken event(s) of {EventsChecked} checked. {Detail}")]
    private partial void LogFailed(int brokenCount, int eventsChecked, string detail);

    [LoggerMessage(
        EventId = 6306,
        Level = LogLevel.Information,
        Message = "AuditChainVerification passed: {EventsChecked} event(s) across {RequestCount} request chain(s).")]
    private partial void LogPassed(int eventsChecked, int requestCount);
}
