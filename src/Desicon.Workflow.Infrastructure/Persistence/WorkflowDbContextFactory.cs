using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Desicon.Workflow.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef` build <see cref="WorkflowDbContext"/> at design time
/// (scaffolding, migrations) without a host project wiring up DI yet. The
/// connection string here is never used to open a connection -- migrations
/// only need it to select the SQL Server provider and generate SQL. Runtime
/// composition (API, Functions) will register its own connection string via
/// configuration.
/// </summary>
public sealed class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(local);Database=DesiconFinanceWorkflow;Trusted_Connection=True;TrustServerCertificate=True;");

        return new WorkflowDbContext(optionsBuilder.Options);
    }
}
