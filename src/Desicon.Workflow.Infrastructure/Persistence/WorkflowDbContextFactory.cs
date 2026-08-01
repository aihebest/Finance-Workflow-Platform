using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Desicon.Workflow.Infrastructure.Persistence;

/// <summary>
/// Lets `dotnet ef` build <see cref="WorkflowDbContext"/> at design time
/// (scaffolding, migrations) without a host project wiring up DI yet. For
/// `migrations add` this connection string is never opened -- it's only
/// used to select the SQL Server provider and generate SQL. `database
/// update` against a local dev database does open it, though, and the
/// ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber migration's ALTER
/// COLUMN requires "Column Encryption Setting=Enabled" on that connection
/// (see WorkflowDbContextFactory and Program.cs's AKV provider
/// registration) -- without it that migration fails, not the ones before
/// it. Runtime composition (API, Functions) registers its own connection
/// string via configuration.
/// </summary>
public sealed class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(local);Database=DesiconFinanceWorkflow;Trusted_Connection=True;TrustServerCertificate=True;Column Encryption Setting=Enabled;");

        return new WorkflowDbContext(optionsBuilder.Options);
    }
}
