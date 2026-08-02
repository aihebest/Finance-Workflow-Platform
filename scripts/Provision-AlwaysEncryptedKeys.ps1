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
    above there. Re-running it is safe: both keys are skipped if present.

    RUNTIME REQUIREMENTS
    --------------------
    PowerShell 7. On Windows PowerShell 5.1 the Always Encrypted cmdlets
    fail with

        MissingMethodException: Method not found:
        'Void Microsoft.Extensions.Caching.Memory.MemoryCache..ctor(...)'

    an assembly-version conflict inside the SqlServer module that no amount
    of credential fixing will resolve. Install PowerShell 7
    (winget install --id Microsoft.PowerShell) and run this file with pwsh.

    Also requires the SqlServer module (Install-Module SqlServer -Scope
    CurrentUser -- installed separately for pwsh, it is not shared with
    Windows PowerShell) and an Azure identity with get/wrapKey/unwrapKey on
    the Key Vault key. See docs/04-Security-and-DevSecOps.md for the Key
    Vault Premium (HSM) requirement.

    WHY -InputObject AND NOT -ConnectionString
    ------------------------------------------
    New-SqlColumnMasterKey and New-SqlColumnEncryptionKey take an SMO
    Database via -InputObject (or a SQLSERVER: provider -Path). Neither
    accepts -ConnectionString; passing one fails with "A parameter cannot be
    found that matches parameter name 'ConnectionString'". Get-SqlDatabase
    -AccessToken produces the object they want, and reuses the caller's
    existing az login rather than prompting.

.PARAMETER SqlServerName
    The Azure SQL logical server hostname, e.g.
    "sql-desicon-fw-dev.database.windows.net".

.PARAMETER DatabaseName
    The target database, e.g. "DesiconFinanceWorkflow".

.PARAMETER KeyVaultKeyUrl
    The full URL of the Key Vault key to use as the CMK. Prefer the
    versionless URL -- a version-pinned CMK breaks on key rotation.

.PARAMETER AccessToken
    Entra access token for https://database.windows.net/. Obtained from the
    Azure CLI when omitted.

.PARAMETER KeyVaultAccessToken
    Entra access token for https://vault.azure.net. Obtained from the Azure
    CLI when omitted.

    Two distinct tokens are required, for two distinct audiences. Creating
    the CMK only writes metadata to the database, so it succeeds with the
    SQL token alone. Creating the CEK generates a key and wraps it with the
    Key Vault key, which is a live data-plane call to Key Vault -- without
    a vault-audience token it fails with

        AKV10000: Request is missing a Bearer, PoP, or MTLS_POP token.
        Status: 401 (Unauthorized)

    That message names Add-SqlAzureAuthenticationContext as the remedy;
    passing -KeyVaultAccessToken is equivalent and does not require an
    interactive prompt.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Provision-AlwaysEncryptedKeys.ps1 `
        -SqlServerName "sql-desicon-fw-dev.database.windows.net" `
        -DatabaseName "DesiconFinanceWorkflow" `
        -KeyVaultKeyUrl "https://kv-desicon-fw-dev.vault.azure.net/keys/cmk-beneficiary-bank-details"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SqlServerName,

    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,

    [Parameter(Mandatory = $true)]
    [string]$KeyVaultKeyUrl,

    [string]$AccessToken,

    [string]$KeyVaultAccessToken,

    [string]$ColumnMasterKeyName = "CMK_Beneficiary_BankDetails",
    [string]$ColumnEncryptionKeyName = "CEK_Beneficiary_BankDetails"
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or later is required -- the Always Encrypted cmdlets throw MissingMethodException on Windows PowerShell 5.1. Re-run with: pwsh -NoProfile -File `"$PSCommandPath`" ..."
}

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer module is required. Install it with: Install-Module -Name SqlServer -Scope CurrentUser -Force"
}

Import-Module SqlServer

if (-not $AccessToken) {
    Write-Host "Acquiring Entra access token via Azure CLI ..."
    $AccessToken = (az account get-access-token `
        --resource https://database.windows.net/ `
        --query accessToken -o tsv)

    if (-not $AccessToken) {
        throw "Could not obtain an access token. Run 'az login' first."
    }
}

if (-not $KeyVaultAccessToken) {
    Write-Host "Acquiring Key Vault access token via Azure CLI ..."
    $KeyVaultAccessToken = (az account get-access-token `
        --resource https://vault.azure.net `
        --query accessToken -o tsv)

    if (-not $KeyVaultAccessToken) {
        throw "Could not obtain a Key Vault access token. Run 'az login' first."
    }
}

# Get-SqlDatabase takes -Name, not -DatabaseName. (Invoke-Sqlcmd, used lower
# down, takes -Database. The three cmdlets disagree; this is not a typo.)
Write-Host "Connecting to $SqlServerName/$DatabaseName ..."
$database = Get-SqlDatabase `
    -ServerInstance $SqlServerName `
    -Name $DatabaseName `
    -AccessToken $AccessToken

Write-Host "Creating column master key settings for $KeyVaultKeyUrl ..."
$cmkSettings = New-SqlAzureKeyVaultColumnMasterKeySettings -KeyURL $KeyVaultKeyUrl

if ($database.ColumnMasterKeys[$ColumnMasterKeyName]) {
    Write-Host "Column master key '$ColumnMasterKeyName' already exists -- skipping."
} else {
    Write-Host "Creating column master key '$ColumnMasterKeyName' ..."
    New-SqlColumnMasterKey `
        -Name $ColumnMasterKeyName `
        -InputObject $database `
        -ColumnMasterKeySettings $cmkSettings | Out-Null
}

$database.ColumnMasterKeys.Refresh()

if ($database.ColumnEncryptionKeys[$ColumnEncryptionKeyName]) {
    Write-Host "Column encryption key '$ColumnEncryptionKeyName' already exists -- skipping."
} else {
    Write-Host "Creating column encryption key '$ColumnEncryptionKeyName' (wrapped via Key Vault) ..."
    New-SqlColumnEncryptionKey `
        -Name $ColumnEncryptionKeyName `
        -InputObject $database `
        -ColumnMasterKeyName $ColumnMasterKeyName `
        -KeyVaultAccessToken $KeyVaultAccessToken | Out-Null
}

# Read back through T-SQL rather than SMO: this is exactly the check the
# migration performs, so a pass here guarantees the migration's THROW guard
# will not fire.
$verify = Invoke-Sqlcmd -ServerInstance $SqlServerName -Database $DatabaseName -AccessToken $AccessToken -Query @"
SELECT
    (SELECT COUNT(*) FROM sys.column_master_keys     WHERE name = '$ColumnMasterKeyName')     AS cmk,
    (SELECT COUNT(*) FROM sys.column_encryption_keys WHERE name = '$ColumnEncryptionKeyName') AS cek;
"@

if ($verify.cmk -eq 0 -or $verify.cek -eq 0) {
    throw "Verification failed: CMK=$($verify.cmk), CEK=$($verify.cek)."
}

Write-Host "Done. CMK and CEK verified present in $DatabaseName." -ForegroundColor Green
