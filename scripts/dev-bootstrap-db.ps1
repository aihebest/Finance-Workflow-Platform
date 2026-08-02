<#
.SYNOPSIS
    Brings an empty dev database up to a working state, in the one order
    that actually succeeds.

.DESCRIPTION
    The steps below are order-dependent and each one fails loudly if its
    predecessor was skipped:

      1. Refresh firewall rule + Entra token (dev-db-connect.ps1).
      2. Create contained users for the App Service and Function App
         managed identities (create-app-user.sql, run once per identity).
      3. Provision the Always Encrypted CMK/CEK from Key Vault
         (Provision-AlwaysEncryptedKeys.ps1). The
         ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber migration
         THROWs if these are absent, so this must precede step 4.
      4. Apply the idempotent migration script.
      5. Pre-create request-number sequences and grant UPDATE on them
         (provision-request-sequences.sql). See that file for why the
         application cannot do this itself.

    Run as an Entra administrator of the SQL logical server. Safe to
    re-run: every step is idempotent.

.PARAMETER KeyVaultKeyUrl
    Full URL of the Key Vault key backing the column master key. Find it
    with:
        az keyvault key list --vault-name kv-desicon-fw-dev --query "[].name" -o tsv

.PARAMETER SkipMigrationScriptGeneration
    Reuse an existing scripts/migrate-dev.sql instead of regenerating it.

.PARAMETER SkipKeyProvisioning
    Skip step 3 because the CMK/CEK already exist, or because they were
    provisioned out of band.

    Out of band is often necessary: on Windows PowerShell 5.1 the Always
    Encrypted cmdlets fail with

        MissingMethodException: Method not found:
        'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(...)'

    which is an assembly-version conflict inside the SqlServer module, not
    a credential or permission fault. Provision the keys from PowerShell 7
    (or SSMS -> Security -> Always Encrypted Keys), then re-run this script
    with -SkipKeyProvisioning. Step 4 verifies the keys exist regardless,
    so skipping cannot mask a missing key.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$KeyVaultKeyUrl,

    [string]$ResourceGroup    = "rg-desicon-fw-dev",
    [string]$SqlServer        = "sql-desicon-fw-dev",
    [string]$DatabaseName     = "DesiconFinanceWorkflow",
    [string]$ApiPrincipal     = "app-desicon-fw-api-dev",
    [string]$FunctionPrincipal = "func-desicon-fw-dev",
    [int]$YearsAhead          = 2,
    [switch]$SkipMigrationScriptGeneration,
    [switch]$SkipKeyProvisioning
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$fqdn = "$SqlServer.database.windows.net"

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. access
Step 1 "Refreshing firewall rule and Entra token"
. (Join-Path $PSScriptRoot "dev-db-connect.ps1") `
    -ResourceGroup $ResourceGroup -SqlServer $SqlServer

# ------------------------------------------------------------- 2. app users
Step 2 "Creating contained users for managed identities"
foreach ($principal in @($ApiPrincipal, $FunctionPrincipal)) {
    Write-Host "  - $principal"
    Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token `
        -InputFile (Join-Path $PSScriptRoot "create-app-user.sql") `
        -Variable @("AppName=$principal", "DatabaseName=$DatabaseName") `
        -ErrorAction Stop -Verbose
}

# ------------------------------------------------- 3. Always Encrypted keys
if ($SkipKeyProvisioning) {
    Step 3 "Skipping Always Encrypted provisioning (-SkipKeyProvisioning)"
} else {
    Step 3 "Provisioning Always Encrypted CMK/CEK"
    & (Join-Path $PSScriptRoot "Provision-AlwaysEncryptedKeys.ps1") `
        -SqlServerName $fqdn `
        -DatabaseName $DatabaseName `
        -KeyVaultKeyUrl $KeyVaultKeyUrl
}

# Checked unconditionally: the ApplyAlwaysEncrypted... migration THROWs on a
# missing key, and a THROW mid-script leaves the schema half-applied. Better
# to stop here with a clear message than to fail inside the migration.
$keyCheck = Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token -Query @"
SELECT
    (SELECT COUNT(*) FROM sys.column_master_keys     WHERE name = 'CMK_Beneficiary_BankDetails') AS cmk,
    (SELECT COUNT(*) FROM sys.column_encryption_keys WHERE name = 'CEK_Beneficiary_BankDetails') AS cek;
"@

if ($keyCheck.cmk -eq 0 -or $keyCheck.cek -eq 0) {
    throw @"
Always Encrypted keys are missing (CMK=$($keyCheck.cmk), CEK=$($keyCheck.cek)).
The ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber migration will THROW.

Provision them from PowerShell 7, then re-run with -SkipKeyProvisioning:

  pwsh -NoProfile -File .\scripts\Provision-AlwaysEncryptedKeys.ps1 ``
      -SqlServerName $fqdn ``
      -DatabaseName $DatabaseName ``
      -KeyVaultKeyUrl $KeyVaultKeyUrl
"@
}

Write-Host "  CMK and CEK present." -ForegroundColor DarkGray

# -------------------------------------------------------------- 4. schema
Step 4 "Applying migrations"
$migrationScript = Join-Path $PSScriptRoot "migrate-dev.sql"

if (-not $SkipMigrationScriptGeneration) {
    dotnet ef migrations script --idempotent `
        --project (Join-Path $root "src\Desicon.Workflow.Infrastructure") `
        --output $migrationScript
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef migrations script failed." }
}

Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token `
    -InputFile $migrationScript -ErrorAction Stop -QueryTimeout 300

# ----------------------------------------------------------- 5. sequences
Step 5 "Provisioning request-number sequences and grants"
Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token `
    -InputFile (Join-Path $PSScriptRoot "provision-request-sequences.sql") `
    -Variable @(
        "ApiPrincipal=$ApiPrincipal",
        "FunctionPrincipal=$FunctionPrincipal",
        "YearsAhead=$YearsAhead"
    ) -ErrorAction Stop -Verbose | Format-Table

# ------------------------------------------------------------ verification
Step 6 "Verifying"
Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token -Query @"
SELECT COUNT(*) AS table_count FROM sys.tables;
"@ | Format-Table

Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token -Query @"
SELECT dp.name AS principal_name, r.name AS role_name
FROM sys.database_principals dp
LEFT JOIN sys.database_role_members m ON m.member_principal_id = dp.principal_id
LEFT JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
WHERE dp.type IN ('E','X')
ORDER BY dp.name, r.name;
"@ | Format-Table

Invoke-Sqlcmd -ServerInstance $fqdn -Database $DatabaseName -AccessToken $token -Query @"
SELECT TOP 1 MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC;
"@ | Format-Table

Write-Host "`nDone." -ForegroundColor Green
