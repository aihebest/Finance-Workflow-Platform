using Azure;
using Azure.Identity;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Desicon.Workflow.Api.HealthChecks;

/// <summary>
/// Readiness probe that exercises the full data path rather than merely
/// opening a socket.
///
/// A "can I connect" check would have passed while four separate
/// misconfigurations made the application non-functional in Azure:
///
///   1. Terraform set ConnectionStrings__Default; Program.cs reads
///      "WorkflowDb". The app fell back to appsettings.json's
///      Server=(local) and failed on first query, not at startup.
///   2. The Azure connection string omitted "Column Encryption Setting=
///      Enabled", so the driver never engaged the Always Encrypted key
///      store provider.
///   3. Both managed identities held "Key Vault Secrets User". Unwrapping a
///      column encryption key is a *key* operation and needs Crypto User.
///   4. The Key Vault network ACL admitted deployer IPs only; the app
///      subnet was never allowed through, and bypass = AzureServices does
///      not cover App Service egress.
///
/// Each is invisible to a connectivity check and fatal to the application.
/// So this probe does three things a trivial one does not: it reads through
/// EF (proving the model maps), it touches the Always Encrypted column
/// (proving the driver, the Key Vault role and the network path all work),
/// and it reads a sequence (proving the UPDATE grant that db_datawriter does
/// not confer).
///
/// Deliberately not a liveness check. Liveness answers "should this instance
/// be restarted"; a database outage is not fixed by restarting the app.
/// </summary>
internal sealed class DatabaseReadinessCheck : IHealthCheck
{
    private readonly WorkflowDbContext _db;

    public DatabaseReadinessCheck(WorkflowDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();

        try
        {
            // 1. Connectivity and authentication as the managed identity.
            if (!await _db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("Cannot connect to the workflow database.");
            }

            data["connection"] = "ok";

            // 2. Schema is present and matches what this build expects. A
            //    pending migration means the deploy job's migrate step did
            //    not run, or ran against a different database.
            var pending = await _db.Database.GetPendingMigrationsAsync(cancellationToken);
            var pendingList = pending.ToList();
            if (pendingList.Count > 0)
            {
                data["pendingMigrations"] = pendingList;
                return HealthCheckResult.Unhealthy(
                    $"{pendingList.Count} migration(s) not applied: {string.Join(", ", pendingList)}",
                    data: data);
            }

            data["migrations"] = "up to date";

            // 3. The Always Encrypted path end to end. Reading the column --
            //    even from an empty table -- forces the driver to fetch and
            //    unwrap the column encryption key via Key Vault, which
            //    exercises the Crypto User role assignment and the network
            //    ACL. A count(*) would not: it never decrypts anything.
            _ = await _db.Beneficiaries
                .OrderBy(b => b.Id)
                .Select(b => b.BankAccountNumber)
                .FirstOrDefaultAsync(cancellationToken);

            data["alwaysEncrypted"] = "ok";

            return HealthCheckResult.Healthy("Database reachable, schema current, encryption path working.", data);
        }
        // No catch-all. HealthCheckService already turns an unhandled
        // exception from a check into an Unhealthy entry carrying the
        // exception, so a `catch (Exception)` here adds nothing but a CA1031
        // violation. These three are caught only because each maps to a
        // specific, recurring misconfiguration worth naming in the response
        // -- anything else is genuinely unexpected and should surface raw.
        catch (SqlException ex)
        {
            data["exception"] = nameof(SqlException);
            data["sqlErrorNumber"] = ex.Number;

            // 18456 login failed -- the contained user does not exist (see
            // scripts/create-app-user.sql). 40615 / v is the firewall.
            // 229/230 permission denied -- typically the missing UPDATE
            // grant on a sequence, which db_datawriter does not confer.
            var hint = ex.Number switch
            {
                18456 => "Login failed. The managed identity may have no contained user -- see scripts/create-app-user.sql.",
                40615 or 40613 => "Rejected by the SQL firewall or the database is unavailable.",
                229 or 230 => "Permission denied. db_datawriter does not cover sequences; check the UPDATE grants from provision-request-sequences.sql.",
                _ => "SQL error."
            };

            data["hint"] = hint;
            return HealthCheckResult.Unhealthy($"{hint} ({ex.Number}: {ex.Message})", ex, data);
        }
        catch (RequestFailedException ex)
        {
            // Key Vault refused the unwrapKey call. 403 is either the
            // missing "Key Vault Crypto User" role -- Secrets User does not
            // cover key operations -- or the network ACL excluding the app
            // subnet. Both produce a 403 and neither is a code fault.
            data["exception"] = nameof(RequestFailedException);
            data["status"] = ex.Status;

            return HealthCheckResult.Unhealthy(
                $"Key Vault refused the column-key unwrap ({ex.Status}). Check the Crypto User role assignment and the vault's network ACL. {ex.Message}",
                ex,
                data);
        }
        catch (AuthenticationFailedException ex)
        {
            // DefaultAzureCredential could not obtain a token at all --
            // no managed identity in Azure, or no az login locally.
            data["exception"] = nameof(AuthenticationFailedException);
            return HealthCheckResult.Unhealthy($"Could not acquire a token for Key Vault. {ex.Message}", ex, data);
        }
    }
}
