<#
.SYNOPSIS
    Rewrites deployer_ip_addresses in an environment's auto.tfvars to the
    machine's current public IP. Run before `terraform plan` / `apply`.

.DESCRIPTION
    Dev drops private endpoints entirely (use_private_endpoints = false),
    so Terraform reaches Key Vault, SQL and Storage over the public
    internet through a narrow IP allow-list -- var.deployer_ip_addresses.

    That creates a failure mode worth understanding, because the error it
    produces points at the wrong thing. Terraform updates the Key Vault's
    network_acls early in the graph, writing whatever is in tfvars. If that
    value is stale, the ACL no longer contains the address Terraform itself
    is calling from -- and the *next* data-plane operation fails with
    "403 ForbiddenByFirewall ... caller is not a trusted service", which
    reads like an RBAC problem but is Terraform having locked itself out
    seconds earlier.

    Addresses are written bare, without a /32 suffix: Storage's
    network_rules.ip_rules rejects /31 and /32 outright, while Key Vault
    and SQL accept either form. See the validation block on
    var.deployer_ip_addresses.

    There is a chicken-and-egg case this script also resolves. Once the ACL
    has gone stale, `terraform plan` fails too, not just apply: refreshing
    azurerm_key_vault_key reads the data plane, so Terraform cannot produce
    a plan that would fix the very ACL locking it out. The Key Vault
    *control* plane is not firewalled, so `az keyvault network-rule add`
    can open the door that Terraform cannot. That is what -OpenKeyVault
    does, and why it defaults on.

.PARAMETER Environment
    Environment directory under infra/terraform/environments. Default dev.

.PARAMETER AdditionalIps
    Extra addresses to keep in the list alongside the current one, e.g. a
    CI runner's egress IP. Bare IPv4, no CIDR suffix.

.PARAMETER OpenKeyVault
    Also add the address to the Key Vault ACL immediately via the control
    plane, so `terraform plan` can refresh existing keys. Default true.
    The subsequent apply reconciles the ACL to exactly what tfvars says --
    which, after this script runs, already includes this address.

.PARAMETER SkipKeyVault
    Suppress the control-plane step (tfvars edit only).
#>
[CmdletBinding()]
param(
    [string]$Environment = "dev",
    [string[]]$AdditionalIps = @(),
    [string]$ResourceGroup = "rg-desicon-fw-dev",
    [string]$KeyVault = "kv-desicon-fw-dev",
    [switch]$SkipKeyVault
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$tfvarsDir = Join-Path $root "infra\terraform\environments\$Environment"
$tfvars    = Join-Path $tfvarsDir "$Environment.auto.tfvars"

if (-not (Test-Path $tfvars)) {
    throw "Not found: $tfvars"
}

$ip = (Invoke-RestMethod "https://api.ipify.org?format=json").ip

foreach ($extra in $AdditionalIps) {
    if ($extra -notmatch '^(\d{1,3}\.){3}\d{1,3}$') {
        throw "AdditionalIps must be bare IPv4 addresses without a CIDR suffix. Got: $extra"
    }
}

$all = @($ip) + $AdditionalIps | Select-Object -Unique
$rendered = 'deployer_ip_addresses = [' + (($all | ForEach-Object { "`"$_`"" }) -join ', ') + ']'

$content = Get-Content $tfvars -Raw

if ($content -notmatch '(?m)^deployer_ip_addresses\s*=.*$') {
    throw "No deployer_ip_addresses assignment found in $tfvars."
}

$existing = [regex]::Match($content, '(?m)^deployer_ip_addresses\s*=.*$').Value

if ($existing -eq $rendered) {
    Write-Host "deployer_ip_addresses already current ($($all -join ', '))." -ForegroundColor DarkGray
} else {
    $updated = [regex]::Replace($content, '(?m)^deployer_ip_addresses\s*=.*$', $rendered)
    Set-Content -Path $tfvars -Value $updated -NoNewline

    Write-Host "  was: $existing"
    Write-Host "  now: $rendered" -ForegroundColor Green
}

# ── Unblock the plan itself ──────────────────────────────────────────────
# Without this, `terraform plan` cannot refresh azurerm_key_vault_key and
# fails with 403 ForbiddenByFirewall before producing a plan.
if (-not $SkipKeyVault) {
    $current = az keyvault show --name $KeyVault -g $ResourceGroup `
        --query "properties.networkAcls.ipRules[].value" -o tsv

    if ($current -match [regex]::Escape("$ip/32")) {
        Write-Host "Key Vault ACL already admits $ip." -ForegroundColor DarkGray
    } else {
        az keyvault network-rule add --name $KeyVault -g $ResourceGroup `
            --ip-address $ip -o none
        Write-Host "Key Vault ACL opened for $ip (control plane)." -ForegroundColor Green
        Start-Sleep -Seconds 15
    }
}

Write-Host "`nRun 'terraform plan -out=tfplan' from $tfvarsDir next." -ForegroundColor Cyan
