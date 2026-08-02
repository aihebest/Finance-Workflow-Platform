using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// Migrations/*_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber.cs runs
/// "ALTER TABLE Beneficiaries ALTER COLUMN BankAccountNumber ... ENCRYPTED
/// WITH (...)" -- an in-place re-encryption that SQL Server only supports
/// via a secure enclave (Always Encrypted with secure enclaves), which is a
/// Windows/VBS-only feature. mcr.microsoft.com/mssql/server:2022-latest is
/// the Linux image, so this specific DDL can never succeed against a
/// Testcontainers instance, no matter how many rows the column has.
///
/// The table is provably empty at the point this migration runs in every
/// fresh test database (nothing writes to Beneficiaries before it), so there
/// is no plaintext data an enclave would need to re-encrypt. This runner
/// applies every migration up to the one before it normally, then -- instead
/// of running that migration's Up() -- drops and re-adds BankAccountNumber
/// already declared ENCRYPTED WITH the same key/algorithm/collation. Adding
/// a new encrypted column never needs an enclave; only altering an existing
/// one in place does. It then hand-records the migration as applied in
/// __EFMigrationsHistory (matching the schema the later migrations' model
/// snapshot expects) and resumes the normal migrator for everything after.
/// This lives in test infrastructure only -- the production migration itself
/// is untouched, and a real environment with enclave support (or one where
/// Provision-AlwaysEncryptedKeys.ps1 has already run) still applies it as
/// authored.
/// </summary>
internal static class EnclaveFreeMigrationRunner
{
    private const string PriorMigrationId = "20260801093600_AddBeneficiaryAndEmployeeBankDetails";
    private const string EncryptedColumnMigrationId = "20260801093942_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber";

    public static async Task MigrateAsync(WorkflowDbContext db, string connectionString, CancellationToken cancellationToken = default)
    {
        var migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(PriorMigrationId, cancellationToken);

        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);

            var productVersion = (string)(await ExecuteScalarAsync(
                connection, "SELECT TOP 1 ProductVersion FROM [__EFMigrationsHistory] ORDER BY [MigrationId] DESC;", cancellationToken))!;

            await ExecuteAsync(connection, """
                ALTER TABLE [Beneficiaries] DROP COLUMN [BankAccountNumber];

                ALTER TABLE [Beneficiaries] ADD [BankAccountNumber] nvarchar(30) COLLATE Latin1_General_BIN2
                ENCRYPTED WITH (
                    COLUMN_ENCRYPTION_KEY = CEK_Beneficiary_BankDetails,
                    ENCRYPTION_TYPE = Deterministic,
                    ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256'
                ) NOT NULL;
                """, cancellationToken);

            await ExecuteAsync(
                connection,
                $"INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ('{EncryptedColumnMigrationId}', '{productVersion}');",
                cancellationToken);
        }

        await migrator.MigrateAsync(cancellationToken: cancellationToken);
    }

    // Every interpolated/embedded value here is either this file's own constant or a
    // ProductVersion string this same method just read back out of __EFMigrationsHistory --
    // never external input.
#pragma warning disable CA2100
    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ExecuteScalarAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }
#pragma warning restore CA2100
}
