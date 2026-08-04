using System.Text.Json;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// Polls the outbox, resolves recipients, renders the template and hands the
/// result to <see cref="INotificationSender"/>. Runs out of band from the
/// transaction that wrote the rows — call DispatchPendingAsync from a
/// scheduled job; nothing here owns its own timer.
/// </summary>
public sealed class OutboxDispatcher
{
    /// <summary>
    /// Attempts before a message is parked as Failed. Five gets through a
    /// transient Graph outage or a throttling window; beyond that the cause
    /// is almost always permanent — a deleted mailbox, a revoked permission,
    /// a recipient who left — and retrying forever hides it.
    /// </summary>
    private const int MaxAttempts = 5;

    private readonly WorkflowDbContext _db;
    private readonly INotificationSender _sender;
    private readonly NotificationRecipientResolver _recipients;
    private readonly NotificationRenderer _renderer;
    private readonly IWorkflowClock _clock;

    public OutboxDispatcher(
        WorkflowDbContext db,
        INotificationSender sender,
        NotificationRecipientResolver recipients,
        NotificationRenderer renderer,
        IWorkflowClock clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Name of the configured transport, for logging by the caller.</summary>
    public string SenderName => _sender.Name;

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
                var prepared = await PrepareAsync(message, cancellationToken);

                if (prepared is null)
                {
                    // PrepareAsync has already recorded why and parked the
                    // message. Nothing is retryable about it.
                    await _db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                await _sender.SendAsync(prepared, cancellationToken);

                message.Status = OutboxMessageStatus.Dispatched;
                message.DispatchedAt = _clock.UtcNow;
                message.LastError = null;
                dispatched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message — a dead recipient, a transient transport
                // failure — must not stop the rest of the batch.
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

    /// <summary>
    /// Resolves recipients and renders the body. Returns null, having parked
    /// the message as Failed, when it can never be sent.
    ///
    /// The distinction matters: a message with no resolvable recipient is not
    /// a transient failure, and running it through five attempts would delay
    /// the discovery by however long the retry window is while producing five
    /// identical log lines that look like a transport problem.
    /// </summary>
    private async Task<NotificationMessage?> PrepareAsync(
        OutboxMessage message, CancellationToken cancellationToken)
    {
        var request = await _db.Requests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == message.RequestId, cancellationToken);

        if (request is null)
        {
            Park(message, $"Request {message.RequestId} no longer exists.");
            return null;
        }

        var specifiers = ParseSpecifiers(message.RecipientRolesJson);
        var resolution = await _recipients.ResolveAsync(specifiers, request, cancellationToken);

        if (resolution.Addresses.Count == 0)
        {
            // Named, not counted. "No recipients" sends whoever investigates
            // looking at the mail transport; "FinanceManager could not be
            // resolved" points straight at the missing role-membership store.
            Park(message,
                resolution.UnresolvedSpecifiers.Count > 0
                    ? $"No recipients: could not resolve {string.Join(", ", resolution.UnresolvedSpecifiers)}."
                    : "No recipients: every specifier resolved to inactive employees or employees with no email address.");

            return null;
        }

        var payload = ParsePayload(message.PayloadJson);
        var (subject, body) = _renderer.Render(message.Template, payload);

        return new NotificationMessage(resolution.Addresses, subject, body, request.RequestNumber);
    }

    private static void Park(OutboxMessage message, string reason)
    {
        message.Status = OutboxMessageStatus.Failed;
        message.LastError = reason;
        message.AttemptCount++;
    }

    private static List<string> ParseSpecifiers(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            // The definition writes this, not a user. Malformed means a
            // definition change went out untested, and an empty list here
            // becomes a named failure one step later rather than an
            // exception that looks like a transport fault.
            return [];
        }
    }

    private static JsonElement ParsePayload(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
