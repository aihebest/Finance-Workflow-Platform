using Desicon.Workflow.Domain.Audit;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Infrastructure.Persistence;

/// <summary>
/// EF Core mapping of the engine and module tables described in
/// docs/03-Data-Model-ERD.md.
///
/// <see cref="Request"/> is table-per-type: this table holds everything the
/// workflow engine needs, and each module (<see cref="ExpenseRequest"/>,
/// <see cref="CashAdvanceRequest"/>) gets its own table sharing the same
/// primary key. <c>Requests</c> is queryable directly for engine-level work
/// (SLA sweeps, actor inbox) without caring which module a row belongs to.
/// </summary>
public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options)
        : base(options)
    {
    }

    public DbSet<Request> Requests => Set<Request>();

    public DbSet<ExpenseRequest> ExpenseRequests => Set<ExpenseRequest>();

    public DbSet<CashAdvanceRequest> CashAdvanceRequests => Set<CashAdvanceRequest>();

    public DbSet<ExpenseLine> ExpenseLines => Set<ExpenseLine>();

    public DbSet<AdvanceLine> AdvanceLines => Set<AdvanceLine>();

    public DbSet<GlPostingLine> GlPostingLines => Set<GlPostingLine>();

    /// <summary>Receipt metadata. The bytes are in blob storage.</summary>
    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Beneficiary> Beneficiaries => Set<Beneficiary>();

    public DbSet<Delegation> Delegations => Set<Delegation>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<AdvanceRetirementLink> AdvanceRetirementLinks => Set<AdvanceRetirementLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
    }
}
