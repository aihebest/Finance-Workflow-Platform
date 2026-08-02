using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Workflow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column master key (backed by Azure Key Vault -- see
            // docs/04-Security-and-DevSecOps.md) and the column encryption
            // key it wraps cannot be created here: producing CEK's encrypted
            // value requires a live call to Key Vault to wrap a randomly
            // generated key, which is exactly what scripts/Provision-
            // AlwaysEncryptedKeys.ps1 does, once per environment, before
            // this migration runs there. A committed migration can't do that
            // itself both because it has no Key Vault credential at
            // migration-apply time and because the wrapped value is
            // different in every environment (dev/test/prod each have their
            // own Key Vault key) -- baking one environment's blob into
            // source control would silently break every other environment.
            // This guard turns "ALTER COLUMN fails with a cryptic error"
            // into a clear pointer at the missing step.
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.column_master_keys WHERE name = 'CMK_Beneficiary_BankDetails')
    THROW 50001, 'CMK_Beneficiary_BankDetails is missing. Run scripts/Provision-AlwaysEncryptedKeys.ps1 against this database before applying this migration.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.column_encryption_keys WHERE name = 'CEK_Beneficiary_BankDetails')
    THROW 50001, 'CEK_Beneficiary_BankDetails is missing. Run scripts/Provision-AlwaysEncryptedKeys.ps1 against this database before applying this migration.', 1;
");

            // Drop-and-re-add rather than ALTER COLUMN ... ENCRYPTED WITH.
            //
            // Encrypting an existing plaintext column *in place* is only
            // supported by Always Encrypted with secure enclaves. Without an
            // enclave SQL Server refuses outright:
            //
            //   Msg 33543: Cannot alter column 'BankAccountNumber'. The
            //   statement attempts to encrypt, decrypt or re-encrypt the
            //   column in-place using a secure enclave, but the current
            //   and/or the target column encryption key for the column is
            //   not enclave-enabled.
            //
            // This is a property of the operation, not of the connection or
            // the row count -- an earlier revision of this file attributed
            // it to a missing "Column Encryption Setting=Enabled", which is
            // wrong and cost an afternoon. Azure SQL only offers enclaves on
            // specific hardware configurations with an attestation endpoint
            // and an enclave-enabled CEK; none of that is provisioned here,
            // and none of it is needed for what this column does.
            //
            // Adding a *new* column already declared ENCRYPTED WITH never
            // needs an enclave, because there is no existing ciphertext or
            // plaintext to convert. So: assert the table is empty, then drop
            // and re-add. tests/.../EnclaveFreeMigrationRunner.cs has used
            // exactly this sequence since the migration was written -- which
            // is why the integration suite passed while the migration itself
            // had never successfully run anywhere. The workaround now lives
            // in the artefact it was compensating for.
            //
            // The emptiness assertion is the load-bearing part. Dropping a
            // populated column would destroy beneficiary bank details
            // silently; failing closed is the only acceptable behaviour. An
            // environment that reaches this migration with rows already
            // present needs client-side re-encryption (SSMS Always Encrypted
            // wizard, or Set-SqlColumnEncryption) or a real enclave, and a
            // considered migration plan -- not this script.
            //
            // Deterministic + BIN2 collation: see the comment on
            // BeneficiaryConfiguration.BankAccountNumber for why (equality
            // search stays possible; range queries don't, which nothing
            // here needs).
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM [Beneficiaries])
    THROW 50002, 'Beneficiaries already contains rows. Dropping BankAccountNumber would destroy plaintext bank details. Re-encrypt client-side (SSMS Always Encrypted wizard or Set-SqlColumnEncryption), or enable secure enclaves, then record this migration as applied by hand.', 1;

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE t.name = N'Beneficiaries'
      AND c.name = N'BankAccountNumber'
      AND c.encryption_type IS NULL
)
BEGIN
    ALTER TABLE [Beneficiaries] DROP COLUMN [BankAccountNumber];

    ALTER TABLE [Beneficiaries] ADD [BankAccountNumber] nvarchar(30) COLLATE Latin1_General_BIN2
    ENCRYPTED WITH (
        COLUMN_ENCRYPTION_KEY = CEK_Beneficiary_BankDetails,
        ENCRYPTION_TYPE = Deterministic,
        ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256'
    ) NOT NULL;
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetrical with Up(): decrypting in place needs an enclave
            // too, so the down path also drops and re-adds. Same reasoning,
            // same guard -- reverting with rows present would silently
            // discard ciphertext that cannot be recovered afterwards.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM [Beneficiaries])
    THROW 50003, 'Beneficiaries contains rows. Reverting this migration would discard encrypted bank details. Decrypt client-side first.', 1;

ALTER TABLE [Beneficiaries] DROP COLUMN [BankAccountNumber];

ALTER TABLE [Beneficiaries] ADD [BankAccountNumber] nvarchar(30) NOT NULL;
");
        }
    }
}
