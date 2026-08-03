using Azure.Identity;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Entry point for the Functions host.
///
/// Named FunctionsHost rather than written as top-level statements, and this
/// is not cosmetic. Top-level statements generate a type called
/// <c>Program</c> in the global namespace. The API does the same, and exposes
/// its Program publicly so WebApplicationFactory&lt;Program&gt; can find it.
/// Once the integration tests referenced both assemblies, that produced
/// CS0433 — "the type 'Program' exists in both" — and WorkflowApiFactory
/// could no longer name the one it meant.
///
/// An explicitly named class emits no <c>Program</c> at all, so the
/// collision cannot recur when the remaining sweeps are added.
/// </summary>
internal static class FunctionsHost
{
    private static async Task Main(string[] args)
    {
        // HostBuilder + ConfigureFunctionsWorkerDefaults, not
        // FunctionsApplication.CreateBuilder: the latter is the Worker 2.x
        // API, and this project stays on Worker 1.22 alongside the rest of
        // the .NET 8 solution.
        var host = new HostBuilder()
            .ConfigureFunctionsWorkerDefaults()
            .ConfigureServices((context, services) =>
            {
                // Same key the API uses, and the same one Terraform sets
                // (ConnectionStrings__WorkflowDb in modules/functions).
                // "Default" was the original name in both places and produced
                // a silent fallback rather than a startup failure — see
                // docs/12-Decision-Log.md.
                var connectionString = context.Configuration.GetConnectionString("WorkflowDb")
                    ?? throw new InvalidOperationException("Connection string 'WorkflowDb' is not configured.");

                // Beneficiary.BankAccountNumber is Always Encrypted with a
                // Key-Vault-backed column master key. Any function that
                // touches Beneficiaries — directly, or through a projection
                // EF widens — needs this provider registered before the first
                // SqlConnection opens, exactly as the API does.
                // DefaultAzureCredential resolves to the Function App's
                // managed identity; the Key Vault Crypto User assignment
                // lives in modules/functions.
                SqlConnection.RegisterColumnEncryptionKeyStoreProviders(
                    new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>
                    {
                        [SqlColumnEncryptionAzureKeyVaultProvider.ProviderName] =
                            new SqlColumnEncryptionAzureKeyVaultProvider(new DefaultAzureCredential())
                    });

                services.AddDbContext<WorkflowDbContext>(options => options.UseSqlServer(connectionString));

                // Holidays are a maintained table, not an algorithm — see
                // WorkingCalendarOptions.Holidays. Seeded for the current and
                // next calendar year so a sweep running in late December
                // still resolves a due date falling in January.
                //
                // Duplicated from the API deliberately for now: the two hosts
                // have genuinely different lifetimes, and a shared
                // AddWorkflowCore() extension is the right refactor once the
                // remaining sweeps land and the shape stops moving. A stale
                // holiday set here produces false overdue flags, and the
                // people who notice first are the ones being wrongly chased.
                services.AddSingleton<IWorkingCalendar>(_ =>
                {
                    var thisYear = DateTime.UtcNow.Year;
                    var holidays = NigerianHolidays.FixedDatesFor(thisYear)
                        .Concat(NigerianHolidays.FixedDatesFor(thisYear + 1))
                        .ToHashSet();

                    return new WorkingCalendar(new WorkingCalendarOptions { Holidays = holidays });
                });

                services.AddSingleton<IWorkflowClock, WorkflowClock>();
            })
            .Build();

        await host.RunAsync();
    }
}
