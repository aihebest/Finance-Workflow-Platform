using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// Records what would have been sent, and sends nothing.
///
/// Used until Exchange provisions the shared mailbox and grants Mail.Send.
/// Without it the outbox could not be drained at all in dev: messages would
/// accumulate as Pending, the dispatcher would never run against real data,
/// and step 7 would sit blocked behind someone else's ticket while looking
/// finished.
///
/// It logs recipients rather than eliding them, because in dev the whole
/// point is to check that resolution produced the people you expected. That
/// is also why this must never be selected in production: recipient
/// addresses in an application log are a disclosure, and a system that
/// reports "sent" while sending nothing is worse than one that visibly
/// fails. Selection is by configuration and asserted in the Functions host —
/// see AddNotifications.
/// </summary>
public sealed partial class LoggingNotificationSender : INotificationSender
{
    private readonly ILogger<LoggingNotificationSender> _logger;

    public LoggingNotificationSender(ILogger<LoggingNotificationSender> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Deliberately explicit. This name appears in the dispatcher's logs so
    /// "the mail never arrived" can be told apart from "nothing was ever
    /// really sent".
    /// </summary>
    public string Name => "logging (no mail is sent)";

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        LogWouldSend(message.RequestNumber, string.Join(", ", message.To), message.Subject);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Information,
        Message = "NOT SENT (logging sender): {RequestNumber} to {Recipients} — \"{Subject}\"")]
    private partial void LogWouldSend(string requestNumber, string recipients, string subject);
}
