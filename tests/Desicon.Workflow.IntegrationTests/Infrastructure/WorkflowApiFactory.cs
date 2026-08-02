using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Scheduling;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// The single WebApplicationFactory&lt;Program&gt; for the whole assembly
/// (see WorkflowApiFixture / IntegrationTestCollection) -- Program.cs calls
/// SqlConnection.RegisterColumnEncryptionKeyStoreProviders once, process-wide,
/// at top level, and that throws InvalidOperationException on a second call,
/// so Program.Main (and therefore this factory's host build) may only run
/// once for the entire test run.
///
/// The connection string is threaded in via the ConnectionStrings__WorkflowDb
/// environment variable rather than ConfigureAppConfiguration: Program.cs
/// reads builder.Configuration.GetConnectionString("WorkflowDb") synchronously
/// at line 20, before builder.Build() -- the point at which
/// WebApplicationFactory's ConfigureAppConfiguration hooks are actually
/// spliced in via HostFactoryResolver. Environment variables, by contrast,
/// are read into WebApplicationBuilder.CreateBuilder(args)'s configuration as
/// soon as it runs, which is early enough. AzureAd:TenantId/Instance/Audience
/// don't need the same treatment: appsettings.json already carries non-null
/// placeholder values that satisfy Program.cs's `?? throw` checks, and real
/// JWT validation never runs because TestAuthHandler replaces it below.
/// </summary>
public sealed class WorkflowApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly FakeTimeProvider _timeProvider;

    public WorkflowApiFactory(string connectionString, FakeTimeProvider timeProvider)
    {
        _connectionString = connectionString;
        _timeProvider = timeProvider;

        Environment.SetEnvironmentVariable("ConnectionStrings__WorkflowDb", _connectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTests");

        builder.ConfigureTestServices(services =>
        {
            // Program.cs registers IWorkingCalendar as a singleton built from
            // NigerianHolidays.FixedDatesFor(...) -- there is no persisted
            // holiday table anywhere in the schema (WorkingCalendarOptions.
            // Holidays is a plain in-memory set), so "seed the holiday table"
            // is satisfied by reusing that same already-holiday-seeded
            // calendar registration untouched; only IWorkflowClock's
            // TimeProvider dependency is swapped for the FakeTimeProvider.
            services.RemoveAll<IWorkflowClock>();
            services.AddSingleton<IWorkflowClock>(sp =>
                new WorkflowClock(sp.GetServices<IWorkingCalendar>(), _timeProvider));

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}
