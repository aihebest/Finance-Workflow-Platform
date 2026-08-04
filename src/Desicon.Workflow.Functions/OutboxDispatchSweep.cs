using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Drains the transactional outbox every five minutes.
///
/// The outbox has had three producers since step 6 — every state change,
/// every reminder, every escalation — and no consumer. Rows have been
/// accumulating as Pending, which looks healthy from the writing side and
/// means nobody has been told anything.
///
/// FIVE MINUTES, NOT HOURLY
/// ------------------------
/// This carries "a request is waiting for you". An hour of latency on that
/// is an hour added to every approval in the chain, which is the delay the
/// platform exists to remove. Five minutes is frequent enough to feel
/// immediate and slow enough that a Graph outage does not become a retry
/// storm.
///
/// The singleton lock matters more here than for the sweeps. Two instances
/// dispatching concurrently would send every notification twice, and unlike
/// a duplicated status recalculation, a duplicate email cannot be withdrawn.
/// </summary>
internal sealed partial class OutboxDispatchSweep
{
    private const string LockResource = "Desicon.Workflow.OutboxDispatch";

    /// <summary>
    /// Batch size per run. Large enough to clear a backlog within a few
    /// ticks, small enough that a run holding the lock does not block the
    /// next one for long.
    /// </summary>
    private const int BatchSize = 100;

    private readonly WorkflowDbContext _db;
    private readonly OutboxDispatcher _dispatcher;
    private readonly IWorkflowClock _clock;
    private readonly ILogger<OutboxDispatchSweep> _logger;

    public OutboxDispatchSweep(
        WorkflowDbContext db,
        OutboxDispatcher dispatcher,
        IWorkflowClock clock,
        ILogger<OutboxDispatchSweep> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _clock = clock;
        _logger = logger;
    }

    [Function(nameof(OutboxDispatchSweep))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
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

        var dispatched = await _dispatcher.DispatchPendingAsync(BatchSize, cancellationToken);

        // Counted after the run, so a growing Failed pile is visible without
        // anyone having to query the table. A dispatcher reporting "0 sent"
        // while a hundred messages sit Failed is the state worth noticing,
        // and it looks identical to "nothing to do" unless this is here.
        var failed = await _db.OutboxMessages
            .CountAsync(m => m.Status == OutboxMessageStatus.Failed, cancellationToken);

        var stillPending = await _db.OutboxMessages
            .CountAsync(m => m.Status == OutboxMessageStatus.Pending, cancellationToken);

        LogComplete(dispatched, stillPending, failed, _dispatcher.SenderName);

        if (failed > 0)
        {
            LogFailedBacklog(failed);
        }
    }

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Information,
        Message = "OutboxDispatchSweep skipped at {Now}: another instance holds {LockResource}.")]
    private partial void LogSkipped(DateTimeOffset now, string lockResource);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Information,
        Message = "OutboxDispatchSweep sent {Dispatched}, {StillPending} pending, {Failed} failed, via {Sender}.")]
    private partial void LogComplete(int dispatched, int stillPending, int failed, string sender);

    [LoggerMessage(
        EventId = 7203,
        Level = LogLevel.Warning,
        Message = "{Failed} outbox message(s) are parked as Failed and will not be retried. Inspect OutboxMessages.LastError.")]
    private partial void LogFailedBacklog(int failed);
}
