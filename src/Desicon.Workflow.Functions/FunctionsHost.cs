using Azure.Identity;
using Desicon.Workflow.Infrastructure.DependencyInjection;
using Desicon.Workflow.Infrastructure.Notifications;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider;
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

                // Workflow definitions ship inside the image — see
                // docker/api.Dockerfile for the equivalent on the API side.
                // The Functions package puts them alongside the assemblies,
                // so the path is relative to the app's base directory rather
                // than a source-tree path.
                var definitionsPath = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    context.Configuration["Workflow:DefinitionsPath"] ?? "modules"));

                // The same registrations the API uses. Previously duplicated
                // here, holiday seeding included, which is the one piece
                // where a silent divergence between the two hosts would move
                // SLA deadlines and retirement due dates without any code
                // looking wrong.
                services.AddWorkflowPlatform(connectionString, definitionsPath);

                // Notifications. UseGraph is explicit configuration rather
                // than inferred from whether a mailbox is set: inference
                // would let a deployment that lost its configuration
                // silently downgrade to sending nothing while reporting
                // success, which is the failure a notification system can
                // least afford.
                var notifications = new NotificationOptions
                {
                    ApplicationBaseUrl = context.Configuration["Notifications:ApplicationBaseUrl"] ?? string.Empty,
                    SenderMailbox = context.Configuration["Notifications:SenderMailbox"] ?? string.Empty
                };

                var useGraph = context.Configuration.GetValue("Notifications:UseGraph", false);

                services.AddNotifications(notifications, new DefaultAzureCredential(), useGraph);
            })
            .Build();

        await host.RunAsync();
    }
}
