using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// Production's Always Encrypted column master key is Key-Vault-backed (see
/// Program.cs's SqlColumnEncryptionAzureKeyVaultProvider registration and
/// scripts/Provision-AlwaysEncryptedKeys.ps1) which requires a live Azure Key
/// Vault and an Azure identity -- neither exists for an ephemeral
/// Testcontainers SQL Server instance. This provisions an equivalent
/// CMK/CEK pair against a throwaway self-signed certificate instead, using
/// the driver's built-in MSSQL_CERTIFICATE_STORE provider. That provider is
/// a "system" provider baked into Microsoft.Data.SqlClient -- unlike the AKV
/// provider it needs no call to
/// SqlConnection.RegisterColumnEncryptionKeyStoreProviders (which may only
/// run once per process and is already spent by Program.cs), so it works
/// for both this one-time provisioning step and every later query the
/// application itself makes against Beneficiary.BankAccountNumber.
/// </summary>
internal static class AlwaysEncryptedTestKeyProvisioner
{
    private const string ColumnMasterKeyName = "CMK_Beneficiary_BankDetails";
    private const string ColumnEncryptionKeyName = "CEK_Beneficiary_BankDetails";

    public static async Task ProvisionAsync(string databaseConnectionString, CancellationToken cancellationToken = default)
    {
        using var certificate = CreateSelfSignedCertificate();
        InstallIntoCurrentUserStore(certificate);

        var masterKeyPath = $"CurrentUser/My/{certificate.Thumbprint}";

        var plainTextKey = RandomNumberGenerator.GetBytes(32);
        var provider = new SqlColumnEncryptionCertificateStoreProvider();
        var encryptedKey = provider.EncryptColumnEncryptionKey(masterKeyPath, "RSA_OAEP", plainTextKey);
        var encryptedValueHex = "0x" + Convert.ToHexString(encryptedKey);

        await using var connection = new SqlConnection(databaseConnectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, $"""
            IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE name = '{ColumnMasterKeyName}')
            CREATE COLUMN MASTER KEY {ColumnMasterKeyName}
            WITH (
                KEY_STORE_PROVIDER_NAME = 'MSSQL_CERTIFICATE_STORE',
                KEY_PATH = '{masterKeyPath}'
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

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Desicon.Workflow.IntegrationTests.AlwaysEncrypted",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));

        // CreateSelfSigned's private key lives only in memory on this X509Certificate2
        // instance. SqlColumnEncryptionCertificateStoreProvider re-opens the certificate
        // from the X509Store by thumbprint later (it doesn't reuse this instance), so the
        // private key needs to actually be persisted alongside the cert in the store, not
        // just attached in-process. Round-tripping through a PFX export/import with
        // PersistKeySet forces that persistence.
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
    }

    private static void InstallIntoCurrentUserStore(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }
}
