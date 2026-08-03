using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// Creates the CMK/CEK pair that
/// Migrations/*_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber.cs
/// requires, against a throwaway Testcontainers SQL Server instance.
///
/// Production's column master key is Key-Vault-backed (see Program.cs and
/// scripts/Provision-AlwaysEncryptedKeys.ps1), which needs a live vault and
/// an Azure identity -- neither exists here. An earlier version used the
/// driver's built-in MSSQL_CERTIFICATE_STORE provider instead, on the
/// reasoning that a "system" provider needs no registration. That is true,
/// but it is implemented against the Windows certificate store and throws
/// PlatformNotSupportedException on Linux, so the suite could never run on
/// a GitHub-hosted runner.
///
/// TestColumnEncryptionKeyStoreProvider replaces it with an in-memory RSA
/// key and no platform dependency at all.
///
/// REGISTRATION ORDER MATTERS. SqlConnection.RegisterColumnEncryptionKey-
/// StoreProviders is process-wide and throws InvalidOperationException on a
/// second call. This must therefore run before WorkflowApiFactory builds the
/// host, and Program.cs skips its own Key Vault registration when the
/// environment is IntegrationTests -- otherwise whichever ran second would
/// throw.
/// </summary>
internal static class AlwaysEncryptedTestKeyProvisioner
{
    private const string ColumnMasterKeyName = "CMK_Beneficiary_BankDetails";
    private const string ColumnEncryptionKeyName = "CEK_Beneficiary_BankDetails";

    private static readonly object RegistrationGate = new();
    private static bool _registered;

    public static async Task ProvisionAsync(string databaseConnectionString, CancellationToken cancellationToken = default)
    {
        RegisterProviderOnce();

        var provider = new TestColumnEncryptionKeyStoreProvider();

        // A fresh 256-bit CEK, wrapped by the in-memory master key. SQL
        // Server stores only the wrapped value; it never sees the plaintext.
        var plainTextKey = RandomNumberGenerator.GetBytes(32);
        var encryptedKey = provider.EncryptColumnEncryptionKey(
            TestColumnEncryptionKeyStoreProvider.MasterKeyPath, "RSA_OAEP", plainTextKey);
        var encryptedValueHex = "0x" + Convert.ToHexString(encryptedKey);

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE name = '{ColumnMasterKeyName}')
            CREATE COLUMN MASTER KEY {ColumnMasterKeyName}
            WITH (
                KEY_STORE_PROVIDER_NAME = '{TestColumnEncryptionKeyStoreProvider.ProviderName}',
                KEY_PATH = '{TestColumnEncryptionKeyStoreProvider.MasterKeyPath}'
            );
            """, cancellationToken);

        await ExecuteAsync(connection, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE name = '{ColumnEncryptionKeyName}')
            CREATE COLUMN ENCRYPTION KEY {ColumnEncryptionKeyName}
            WITH VALUES (
                COLUMN_MASTER_KEY = {ColumnMasterKeyName},
                ALGORITHM = 'RSA_OAEP',
                ENCRYPTED_VALUE = {encryptedValueHex}
            );
            """, cancellationToken);
    }

    private static void RegisterProviderOnce()
    {
        lock (RegistrationGate)
        {
            if (_registered)
            {
                return;
            }

            SqlConnection.RegisterColumnEncryptionKeyStoreProviders(
                new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(StringComparer.OrdinalIgnoreCase)
                {
                    [TestColumnEncryptionKeyStoreProvider.ProviderName] = new TestColumnEncryptionKeyStoreProvider()
                });

            _registered = true;
        }
    }

    // The interpolated values are this class's own generated key names/paths/
    // hex blobs, never external input -- CA2100 can't see that, so this is a
    // deliberate, scoped suppression rather than a missing parameterization.
#pragma warning disable CA2100
    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
#pragma warning restore CA2100
}
