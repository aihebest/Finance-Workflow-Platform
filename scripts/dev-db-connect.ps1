<#
.SYNOPSIS
    Dot-source before any local work against the dev environment. Refreshes
    the SQL firewall rule, the Key Vault network rule, and the Entra token.

.DESCRIPTION
    Three things expire constantly during local development against Azure:

      * the developer's public IP -- dynamic on most Nigerian ISP links,
        observed changing four times in one hour
      * the Azure SQL server firewall rule pinned to that IP
      * the Entra access token used for AAD-authenticated SQL connections
        (~1 hour lifetime)

    And a fourth, less obvious one: kv-desicon-fw-dev has
    networkAcls.defaultAction = Deny, so Key Vault data-plane calls
    (Provision-AlwaysEncryptedKeys.ps1, az keyvault key list) fail with
    ForbiddenByFirewall from an unlisted IP -- a *network* denial that
    looks like a permissions error but is not.

    Dot-source it, or $token will not survive into your session:

        . .\scripts\dev-db-connect.ps1

    IP-rule hygiene: Key Vault ipRules accumulate silently, one stale /32
    per IP change. This script records the address it last added in
    $env:USERPROFILE\.desicon-dev-ip and removes that entry before adding
    the current one, so the ACL stays at a single developer rule. Rules
    added by anyone or anything else are left untouched.

    NOTE ON DRIFT: both the SQL firewall rule and the Key Vault network ACL
    are Terraform-managed. Changes made here are reverted by the next
    terraform apply. For anything longer-lived than a local session, add
    the address to the dev tfvars instead.

.PARAMETER SkipKeyVault
    Only refresh the SQL firewall rule and token.
#>
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-desicon-fw-dev",
    [string]$SqlServer     = "sql-desicon-fw-dev",
    [string]$KeyVault      = "kv-desicon-fw-dev",
    [string]$RuleName      = "aihe-laptop",
    [switch]$SkipKeyVault
)

$ErrorActionPreference = "Stop"

$ip = (Invoke-RestMethod "https://api.ipify.org?format=json").ip
$stateFile = Join-Path $env:USERPROFILE ".desicon-dev-ip"
$previousIp = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw).Trim() } else { $null }

# ── SQL server firewall ──────────────────────────────────────────────────
$ruleExists = az sql server firewall-rule list -g $ResourceGroup -s $SqlServer `
    --query "[?name=='$RuleName'] | length(@)" -o tsv

if ($ruleExists -eq "0") {
    az sql server firewall-rule create -g $ResourceGroup -s $SqlServer `
        -n $RuleName --start-ip-address $ip --end-ip-address $ip -o none
} else {
    az sql server firewall-rule update -g $ResourceGroup -s $SqlServer `
        -n $RuleName --start-ip-address $ip --end-ip-address $ip -o none
}
Write-Host "SQL firewall rule '$RuleName' -> $ip" -ForegroundColor Green

# ── Key Vault network ACL ────────────────────────────────────────────────
if (-not $SkipKeyVault) {
    if ($previousIp -and $previousIp -ne $ip) {
        az keyvault network-rule remove --name $KeyVault -g $ResourceGroup `
            --ip-address "$previousIp/32" -o none 2>$null
        Write-Host "Key Vault: removed stale rule $previousIp/32" -ForegroundColor DarkGray
    }

    az keyvault network-rule add --name $KeyVault -g $ResourceGroup `
        --ip-address $ip -o none
    Write-Host "Key Vault network rule -> $ip" -ForegroundColor Green

    $ip | Set-Content -Path $stateFile -NoNewline

    # Key Vault ACL changes are eventually consistent; data-plane calls made
    # immediately after can still be refused.
    Start-Sleep -Seconds 15

    $rules = az keyvault show --name $KeyVault -g $ResourceGroup `
        --query "properties.networkAcls.ipRules[].value" -o tsv
    if ($rules) {
        Write-Host "Key Vault ipRules now: $($rules -join ', ')" -ForegroundColor DarkGray
    }
}

# ── Entra token for SQL ──────────────────────────────────────────────────
$fetched = (az account get-access-token `
    --resource https://database.windows.net/ `
    --query accessToken -o tsv)

Set-Variable -Name token -Value $fetched -Scope Global

Write-Host "Entra SQL token refreshed (valid ~1h)." -ForegroundColor Green
