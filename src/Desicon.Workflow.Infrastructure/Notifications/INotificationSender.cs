namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// One outbound notification: addressed, rendered, ready to transmit.
/// </summary>
/// <param name="To">Recipient addresses. Never empty — the dispatcher fails a
/// message with no resolvable recipient rather than sending it nowhere.</param>
/// <param name="Subject">Rendered subject line.</param>
/// <param name="HtmlBody">Rendered HTML body, including the deep link.</param>
/// <param name="RequestNumber">For logging and correlation.</param>
public sealed record NotificationMessage(
    IReadOnlyList<string> To,
    string Subject,
    string HtmlBody,
    string RequestNumber);

/// <summary>
/// Transmits a rendered notification. Transport only.
///
/// This deliberately does not take an <c>OutboxMessage</c>, as an earlier
/// draft of the interface did. That would make every implementation
/// responsible for resolving recipient specifiers and rendering templates as
/// well as sending — three concerns in one seam, two of them identical
/// across every transport. The dispatcher resolves and renders; a sender
/// decides only how bytes leave the building.
///
/// Implementations are not required to be idempotent. The dispatcher marks a
/// message Dispatched only after a send returns, so a crash in between
/// resends. Duplicate mail is the acceptable failure; silently losing an
/// approval request is not.
/// </summary>
public interface INotificationSender
{
    /// <summary>Identifies the transport in logs, so "nothing arrived" can be
    /// told apart from "nothing was ever really sent".</summary>
    string Name { get; }

    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
