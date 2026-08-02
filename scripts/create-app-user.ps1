<#
.SYNOPSIS
    Post-apply step: creates a contained database user for an app's managed
    identity and grants it db_datareader/db_datawriter.

.DESCRIPTION
    infra/terraform/modules/sql provisions the Azure SQL server and database
    with Entra-ID-only authentication -- there is no SQL login, and Terraform
    cannot run CREATE USER ... FROM EXTERNAL PROVIDER because that statement
    requires an authenticated connection to the database itself, not a call
    to the ARM API. It must run as an Entra ID administrator on the server
    (see the server's azuread_administrator block / entra_admin_object_id).

    Run this once per app identity per environment, after `terraform apply`
    and before the app's first deployment. Re-running it is safe: the
    underlying create-app-user.sql skips the CREATE USER and role grants if
    the user already exists / is already a member.

    Requires the SqlServer PowerShell module (Install-Module SqlServer) and
    an Az PowerShell session (Connect-AzAccount) authenticated as a member
    of the server's Entra ID administrator group.

.PARAMETER SqlServerName
    The SQL logical server hostname, e.g. "sql-desicon-fw-dev.database.windows.net"
    -- see the sql module's server_fqdn output, or the dev environment's
    sql_server_fqdn output.

.PARAMETER DatabaseName
    The target database, e.g. "DesiconFinanceWorkflow" -- see the sql
    module's database_name output.

.PARAMETER AppName
    The name of the App Service or Function App whose system-assigned
    managed identity is being granted access. This must exactly match the
    Azure resource name -- that is also the identity's display name in
    Entra ID, which is what CREATE USER ... FROM EXTERNAL PROVIDER resolves.

.EXAMPLE
    ./create-app-user.ps1 `
        -SqlServerName "sql-desicon-fw-dev.database.windows.net" `
        -DatabaseName "DesiconFinanceWorkflow" `
        -AppName "app-desicon-fw-api-dev"

.EXAMPLE
    ./create-app-user.ps1 `
        -SqlServerName "sql-desicon-fw-dev.database.windows.net" `
        -DatabaseName "DesiconFinanceWorkflow" `
        -AppName "func-desicon-fw-dev"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SqlServerName,

    [Parameter(Mandatory = $true)]
    [string]$DatabaseName,

    [Parameter(Mandatory = $true)]
    [string]$AppName
)

$ErrorActionPreference = "Stop"

if (-not (Get-Module -ListAvailable -Name SqlServer)) {
    throw "The SqlServer PowerShell module is required. Install it with: Install-Module SqlServer -Scope CurrentUser"
}

$context = Get-AzContext
if (-not $context) {
    throw "Not logged in to Azure. Run Connect-AzAccount as a member of the SQL server's Entra ID administrator group first."
}
Write-Host "Connecting as: $($context.Account.Id)"

$accessToken = (Get-AzAccessToken -ResourceUrl "https://database.windows.net/").Token

$scriptPath = Join-Path $PSScriptRoot "create-app-user.sql"

Write-Host "Granting [$AppName] access to $DatabaseName on $SqlServerName..."
Invoke-Sqlcmd `
    -ServerInstance $SqlServerName `
    -Database $DatabaseName `
    -AccessToken $accessToken `
    -InputFile $scriptPath `
    -Variable @("AppName=$AppName", "DatabaseName=$DatabaseName") `
    -Verbose

Write-Host "Done."
