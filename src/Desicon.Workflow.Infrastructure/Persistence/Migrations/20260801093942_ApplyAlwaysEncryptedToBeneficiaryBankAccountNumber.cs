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

            // Requires a connection with "Column Encryption Setting=Enabled"
            // (see appsettings.json and Program.cs's AKV provider
            // registration) -- SQL Server rejects this DDL over a connection
            // that isn't Always-Encrypted-aware, even though the table has
            // no rows to re-encrypt yet. Deterministic + BIN2 collation:
            // see the comment on BeneficiaryConfiguration.BankAccountNumber
            // for why (equality search stays possible; range queries don't,
            // which nothing here needs).
            migrationBuilder.Sql(@"
ALTER TABLE [Beneficiaries]
ALTER COLUMN [BankAccountNumber] nvarchar(30) COLLATE Latin1_General_BIN2
ENCRYPTED WITH (
    COLUMN_ENCRYPTION_KEY = CEK_Beneficiary_BankDetails,
    ENCRYPTION_TYPE = Deterministic,
    ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256'
) NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE [Beneficiaries]
ALTER COLUMN [BankAccountNumber] nvarchar(30) NOT NULL;
");
        }
    }
}
