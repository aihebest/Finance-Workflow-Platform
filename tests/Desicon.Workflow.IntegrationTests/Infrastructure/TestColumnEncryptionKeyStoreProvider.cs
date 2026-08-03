using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>
/// An in-process Always Encrypted key store backed by an ephemeral RSA key.
///
/// This replaces SqlColumnEncryptionCertificateStoreProvider
/// (MSSQL_CERTIFICATE_STORE), which is implemented against the Windows
/// certificate store and throws PlatformNotSupportedException on Linux. The
/// suite therefore passed on developer machines and could not run on
/// ubuntu-latest at all -- invisible for as long as CI failed earlier, at
/// the build step.
///
/// Production uses SqlColumnEncryptionAzureKeyVaultProvider against a
/// Key-Vault-backed CMK. Neither that nor a certificate store is available
/// to an ephemeral Testcontainers instance, and what the tests actually need
/// is not a *particular* key store but a working one: that the column is
/// genuinely encrypted, that the driver engages the encryption path, and
/// that reads and writes round-trip. An RSA key held in memory for the
/// lifetime of the test run satisfies all three and needs no platform
/// facility whatsoever.
///
/// The key is static and never persisted. It exists only while the test
/// process runs, so ciphertext in the throwaway database is unrecoverable
/// afterwards -- which is the correct property for test data that models
/// beneficiary bank details.
/// </summary>
internal sealed class TestColumnEncryptionKeyStoreProvider : SqlColumnEncryptionKeyStoreProvider
{
    /// <summary>Provider name recorded in sys.column_master_keys.</summary>
    public const string ProviderName = "DESICON_TEST_STORE";

    /// <summary>
    /// Nominal key path. The provider ignores it -- there is one key -- but
    /// SQL Server requires CREATE COLUMN MASTER KEY to record something, and
    /// the driver passes it back on every decrypt.
    /// </summary>
    public const string MasterKeyPath = "desicon-test/in-memory-rsa";

    // Static so that the key used to wrap the CEK during provisioning is the
    // same key used to unwrap it later, from the application's own
    // connections, in the same process.
    private static readonly RSA MasterKey = RSA.Create(2048);

    // RSA_OAEP in SQL Server's Always Encrypted means OAEP with SHA-1, not
    // SHA-256. Using SHA-256 here produces a blob the server accepts and the
    // driver then fails to decrypt, with an error that points at the key
    // rather than at the padding.
    private static readonly RSAEncryptionPadding Padding = RSAEncryptionPadding.OaepSHA1;

    public override byte[] EncryptColumnEncryptionKey(
        string masterKeyPath, string encryptionAlgorithm, byte[] columnEncryptionKey)
    {
        ArgumentNullException.ThrowIfNull(columnEncryptionKey);
        EnsureAlgorithm(encryptionAlgorithm);

        return MasterKey.Encrypt(columnEncryptionKey, Padding);
    }

    public override byte[] DecryptColumnEncryptionKey(
        string masterKeyPath, string encryptionAlgorithm, byte[] encryptedColumnEncryptionKey)
    {
        ArgumentNullException.ThrowIfNull(encryptedColumnEncryptionKey);
        EnsureAlgorithm(encryptionAlgorithm);

        return MasterKey.Decrypt(encryptedColumnEncryptionKey, Padding);
    }

    // Metadata signing is only exercised by enclave-enabled column master
    // keys. Nothing here is enclave-enabled -- see the migration comment on
    // ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber -- so these should
    // never be reached. Throwing rather than returning a placeholder means
    // that if enclaves are ever introduced, this fails loudly instead of
    // silently accepting unsigned metadata.
    public override byte[] SignColumnMasterKeyMetadata(string masterKeyPath, bool allowEnclaveComputations) =>
        throw new NotSupportedException(
            "The test key store does not sign column master key metadata. Only enclave-enabled keys require it, and none are used here.");

    public override bool VerifyColumnMasterKeyMetadata(
        string masterKeyPath, bool allowEnclaveComputations, byte[] signature) =>
        throw new NotSupportedException(
            "The test key store does not verify column master key metadata. Only enclave-enabled keys require it, and none are used here.");

    private static void EnsureAlgorithm(string encryptionAlgorithm)
    {
        if (!string.Equals(encryptionAlgorithm, "RSA_OAEP", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported key encryption algorithm '{encryptionAlgorithm}'.");
        }
    }
}
