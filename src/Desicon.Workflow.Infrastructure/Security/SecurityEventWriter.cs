using Desicon.Workflow.Domain.Security;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Security;

/// <summary>
/// Opens a brand-new WorkflowDbContext (and therefore a brand-new database
/// connection) per write, rather than reusing whatever scoped WorkflowDbContext
/// the caller already holds. That is the entire point: RequestActionService
/// calls this after rolling back the ambient transaction for a denied action,
/// and a denial record written on the same connection/transaction as the
/// thing it is reporting on would vanish along with the rollback it is
/// supposed to survive.
/// </summary>
public sealed class SecurityEventWriter : ISecurityEventWriter
{
    private readonly DbContextOptions<WorkflowDbContext> _options;

    public SecurityEventWriter(DbContextOptions<WorkflowDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);

        await using var db = new WorkflowDbContext(_options);
        db.SecurityEvents.Add(securityEvent);
        await db.SaveChangesAsync(cancellationToken);
    }
}
