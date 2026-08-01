<#
.SYNOPSIS
    One-time, per-environment provisioning of the Always Encrypted column
    master key (CMK) and column encryption key (CEK) used to encrypt
    Beneficiary.BankAccountNumber.

.DESCRIPTION
    Migrations/*_ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber.cs applies
    the ALTER COLUMN ... ENCRYPTED WITH (...) statement, but it cannot create
    the CMK/CEK themselves: the CEK's encrypted value is produced by wrapping
    a freshly generated key with Azure Key Vault, which requires a live,
    authenticated call to Key Vault -- not something a committed SQL script
    can do, and not something that would be safe to commit even if it could
    (each environment's Key Vault produces a different wrapped value, so a
    value baked in for one environment is simply wrong for every other one).

    Run this once per environment (dev, test, staging, prod), against that
    environment's database and Key Vault, before applying the migration
    above there. Re-running it is safe: both New-SqlColumnMasterKey and
    New-SqlColumnEncryptionKey are skipped if the key already exists.

    Requires the SqlServer PowerShell module (Install-Module SqlServer) and
    an Azure identity (Connect-AzAccount, or Managed Identity when run from
    a pipeline) with "get"/"wrapKey"/"unwrapKey" permission on the Key Vault
    key -- see docs/04-Security-and-DevSecOps.md for the Key Vault Premium
    (HSM) requirement.

.PARAMETER SqlServerName
    The SQL Server (or Azure SQL logical server) hostname, e.g.
    "myserver.database.windows.net".

.PARAMETER DatabaseName
    The target database, e.g. "DesiconFinanceWorkflow".

.PARAMETER KeyVaultKeyUrl
    The full URL of the Key Vault key to use as the CMK, e.g.
    "https://my-vault.vault.azure.net/keys/cmk-beneficiary-bank-details".

.EXAMPLE
    ./Provision-AlwaysEncryptedKeys.ps1 `
        -SqlServerName "desicon-dev.database.windows.net" `
        -DatabaseName "DesiconFinanceWorkflow" `
        -KeyVaultKeyUrl "https://desicon-dev-kv.vault.azure.net/keys/cmk-beneficiary-bank-details"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SqlServerName,

    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,

    [Parameter(Mandatory = $true)]
    [string]$KeyVaultKeyUrl,

    [string]$ColumnMasterKeyName = "CMK_Beneficiary_BankDetails",
    [string]$ColumnEncryptionKeyName = "CEK_Beneficiary_BankDetails"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer module is required. Install it with: Install-Module -Name SqlServer -Scope CurrentUser"
}

Import-Module SqlServer

$connectionString = "Server=tcp:$SqlServerName,1433;Database=$DatabaseName;Authentication=Active Directory Default;"

Write-Host "Creating column master key settings for $KeyVaultKeyUrl ..."
$cmkSettings = New-SqlAzureKeyVaultColumnMasterKeySettings -KeyURL $KeyVaultKeyUrl

$existingCmk = Invoke-Sqlcmd -ConnectionString $connectionString `
    -Query "SELECT name FROM sys.column_master_keys WHERE name = '$ColumnMasterKeyName'"

if ($existingCmk) {
    Write-Host "Column master key '$ColumnMasterKeyName' already exists -- skipping."
} else {
    Write-Host "Creating column master key '$ColumnMasterKeyName' ..."
    New-SqlColumnMasterKey -Name $ColumnMasterKeyName `
        -ConnectionString $connectionString `
        -ColumnMasterKeySettings $cmkSettings
}

$existingCek = Invoke-Sqlcmd -ConnectionString $connectionString `
    -Query "SELECT name FROM sys.column_encryption_keys WHERE name = '$ColumnEncryptionKeyName'"

if ($existingCek) {
    Write-Host "Column encryption key '$ColumnEncryptionKeyName' already exists -- skipping."
} else {
    Write-Host "Creating column encryption key '$ColumnEncryptionKeyName' (wrapped via Key Vault) ..."
    New-SqlColumnEncryptionKey -Name $ColumnEncryptionKeyName `
        -ConnectionString $connectionString `
        -ColumnMasterKeyName $ColumnMasterKeyName
}

Write-Host "Done. Apply the ApplyAlwaysEncryptedToBeneficiaryBankAccountNumber migration against $DatabaseName next."
