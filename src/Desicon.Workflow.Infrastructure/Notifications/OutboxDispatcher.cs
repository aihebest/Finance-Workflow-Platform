using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// Polls the outbox and hands pending rows to <see cref="INotificationSender"/>.
/// Runs out of band from the transaction that wrote the rows -- call
/// DispatchPendingAsync from a scheduled job or hosted service; nothing here
/// owns its own timer.
/// </summary>
public sealed class OutboxDispatcher
{
    private const int MaxAttempts = 5;

    private readonly WorkflowDbContext _db;
    private readonly INotificationSender _sender;
    private readonly IWorkflowClock _clock;

    public OutboxDispatcher(WorkflowDbContext db, INotificationSender sender, IWorkflowClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<int> DispatchPendingAsync(
        int batchSize = 50, CancellationToken cancellationToken = default)
    {
        var pending = await _db.OutboxMessages
            .Where(m => m.Status == OutboxMessageStatus.Pending)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var dispatched = 0;

        foreach (var message in pending)
        {
            try
            {
                await _sender.SendAsync(message, cancellationToken);
                message.Status = OutboxMessageStatus.Dispatched;
                message.DispatchedAt = _clock.UtcNow;
                message.LastError = null;
                dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message (a dead recipient, a transient transport
                // failure) must not stop the rest of the batch.
                message.AttemptCount++;
                message.LastError = ex.Message;

                if (message.AttemptCount >= MaxAttempts)
                {
                    message.Status = OutboxMessageStatus.Failed;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return dispatched;
    }
}
