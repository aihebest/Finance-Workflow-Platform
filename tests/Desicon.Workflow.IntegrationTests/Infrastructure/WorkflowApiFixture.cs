using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Respawn;
using Respawn.Graph;
using Testcontainers.MsSql;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// Assembly-wide fixture: one Testcontainers SQL Server 2022 instance, one
/// WorkflowApiFactory, one FakeTimeProvider, shared by every test class via
/// IntegrationTestCollection. Must stay singular -- see WorkflowApiFactory's
/// remarks on Program.cs's one-time AKV provider registration.
/// </summary>
public sealed class WorkflowApiFixture : IAsyncLifetime
{
    private const string DatabaseName = "DesiconFinanceWorkflowTests";

    private MsSqlContainer _container = null!;
    private Respawner _respawner = null!;
    private string _connectionString = null!;

    public WorkflowApiFactory Factory { get; private set; } = null!;

    // A Monday, so SLA/working-calendar arithmetic in tests starts from a
    // predictable, non-weekend, non-holiday instant. Both EXPENSE and
    // CASH_ADVANCE (modules/*.workflow.json) declare "effectiveFrom":
    // "2026-09-01T00:00:00Z" at the module level and on their
    // PAYMENT_METHOD_THRESHOLD_NGN policy row -- WorkflowDefinitionExtensions
    // .GetPolicyValue throws if asOf is before that, so the fixed clock must
    // land on or after it.
    public FakeTimeProvider TimeProvider { get; } =
        new(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.FromHours(1)));

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        await _container.StartAsync();

        await CreateDatabaseAsync();

        var builder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = DatabaseName,
            TrustServerCertificate = true,
            ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled
        };
        _connectionString = builder.ConnectionString;

        await AlwaysEncryptedTestKeyProvisioner.ProvisionAsync(_connectionString);

        Factory = new WorkflowApiFactory(_connectionString, TimeProvider);

        // The normal migrator, deliberately. This used to call
        // EnclaveFreeMigrationRunner, which skipped the
        // ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber migration and
        // hand-applied an equivalent schema change instead, because that
        // migration's in-place ALTER COLUMN needed a secure enclave the
        // Linux SQL Server image cannot provide.
        //
        // The consequence was a suite that passed against a schema the
        // shipped migration could not actually produce -- and it could not
        // produce it anywhere, not just in tests: Azure SQL has no enclave
        // here either, so the migration had never once run successfully.
        // The workaround was load-bearing and the artefact it compensated
        // for was broken, which is the worst arrangement of the two.
        //
        // The migration now performs the same guarded drop-and-re-add
        // itself, so running the real migrator exercises exactly what
        // deploys. If this line ever needs special-casing again, that is a
        // signal the migration is wrong, not the test.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            await db.Database.MigrateAsync();
        }

        await using var respawnConnection = new SqlConnection(_connectionString);
        await respawnConnection.OpenAsync();
        _respawner = await Respawner.CreateAsync(respawnConnection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            TablesToIgnore = [new Table("__EFMigrationsHistory")]
        });
    }

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    public async Task DisposeAsync()
    {
        // InitializeAsync can throw before Factory/_container are assigned (e.g. the
        // container started but a later provisioning step failed) -- xunit still calls
        // DisposeAsync in that case, so guard against partially-initialized state instead
        // of masking the real failure behind an unrelated NullReferenceException.
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public IServiceScope CreateScope() => Factory.Services.CreateScope();

    public HttpClient CreateClient(Employee actingAs, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ObjectIdHeader, actingAs.EntraObjectId);

        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", roles));
        }

        return client;
    }

    private async Task CreateDatabaseAsync()
    {
        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{DatabaseName}];";
        await command.ExecuteNonQueryAsync();
    }
}

// xunit's collection-fixture convention names this type after the collection
// it defines; CA1711 doesn't have a carve-out for that convention.
#pragma warning disable CA1711
[CollectionDefinition(Name)]
public sealed class IntegrationTestCollection : ICollectionFixture<WorkflowApiFixture>
{
    public const string Name = "Workflow API";
}
#pragma warning restore CA1711
