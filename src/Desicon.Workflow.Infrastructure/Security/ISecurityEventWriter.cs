using Desicon.Workflow.Domain.Security;

namespace Desicon.Workflow.Infrastructure.Security;

/// <summary>
/// Writes a denial record durably, independent of whatever transaction the
/// denied action was attempted under. See SecurityEventWriter.
/// </summary>
public interface ISecurityEventWriter
{
    Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default);
}
