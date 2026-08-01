using Desicon.Workflow.Domain.Notifications;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// The actual send. No implementation ships in this project -- there is no
/// Employee directory yet to resolve OutboxMessage.RecipientRolesJson's
/// specifiers ("CurrentActor", "Requester", ...) into real addresses, and no
/// email/SMS transport configured. A real implementation is a deployment-time
/// concern, same as the AuditEvent GRANT/DENY note.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
