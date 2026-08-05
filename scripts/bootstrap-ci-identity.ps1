<#
.SYNOPSIS
    One-time creation of the Entra application GitHub Actions federates as,
    with the role assignments deploy-app.yml needs. No client secret is ever
    created.

.DESCRIPTION
    OIDC federation, not a service principal password. GitHub presents a
    short-lived token describing the repository, branch and workflow; Entra
    exchanges it for an Azure token if the subject matches a federated
    credential registered here. Nothing long-lived is stored in GitHub, so
    there is no secret to rotate, leak or find in a log.

    The subject is exact-match, not a pattern. A credential for
    refs/heads/main does not authorise a pull request build, which is the
    point: a fork's PR cannot obtain a token for your subscription.

    ROLES GRANTED, AND WHY EACH IS NEEDED
    -------------------------------------
    Contributor on the ACR      -- az acr build creates a task run, which
                                   AcrPush alone cannot do. Scoped to the
                                   registry, not the resource group.
    Website Contributor on the  -- set the container image and restart.
      App Service                  Cannot read app settings or secrets.
    SQL Server Contributor on   -- open and close the runner's firewall
      the SQL server               exception around the migration step.
                                   Grants no data-plane access: the
                                   database user is created separately by
                                   scripts/create-migration-user.sql.

    Deliberately NOT Contributor on the resource group: that would let a
    compromised workflow rewrite the infrastructure it deploys into.

.PARAMETER Repository
    owner/repo, e.g. "aihebest/Finance-Workflow-Platform".

.PARAMETER Branch
    Branch the workflow runs from. One federated credential per branch.

.PARAMETER Subject
    Overrides the computed subject. Needed because GitHub does not always
    emit the documented "repo:owner/repo:ref:refs/heads/main" form. Some
    accounts present an ID-qualified subject instead:

        repo:aihebest@144573775/Finance-Workflow-Platform@1318978303:ref:refs/heads/main

    which embeds the numeric owner and repository IDs. That form is better
    -- it survives a rename and cannot be claimed by someone who registers
    a deleted repository name -- but it does not match a credential
    registered for the documented form, and the mismatch fails with
    "AADSTS700213: No matching federated identity record found for presented
    assertion subject", quoting the string it wanted.

    So: run the workflow once, read the subject out of the azure/login step
    ("subject claim - ..."), and re-run this script passing it verbatim.

.NOTES
    If the repository moves to a Desicon organisation (an open decision in
    docs/12-Decision-Log.md), the federated credential subject changes with
    it and must be re-registered -- the old one silently stops matching, and
    the failure appears as "no matching federated identity record found".
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [string]$Branch = "main",
    [string]$Subject,
    [string]$DisplayName = "github-desicon-finance-workflow",
    [string]$ResourceGroup = "rg-desicon-fw-dev",
    [string]$AcrName = "crdesiconfwdev",
    [string]$AppServiceName = "app-desicon-fw-api-dev",
    [string]$FunctionAppName = "func-desicon-fw-dev",
    [string]$WebAppName = "app-desicon-fw-web-dev",
    [string]$FunctionStorageAccount = "stdesiconfwdevfn2",
    [string]$SqlServerName = "sql-desicon-fw-dev"
)

$ErrorActionPreference = "Stop"

# ── Application + service principal ──────────────────────────────────────
$existing = az ad app list --display-name $DisplayName --query "[0].appId" -o tsv

if ($existing) {
    Write-Host "Application '$DisplayName' already exists ($existing)." -ForegroundColor DarkGray
    $appId = $existing
} else {
    Write-Host "Creating application '$DisplayName' ..."
    $appId = az ad app create --display-name $DisplayName --query appId -o tsv
}

$spId = az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv
if (-not $spId) {
    Write-Host "Creating service principal ..."
    $spId = az ad sp create --id $appId --query id -o tsv
}

# ── Federated credential ─────────────────────────────────────────────────
$subject = if ($Subject) { $Subject } else { "repo:${Repository}:ref:refs/heads/$Branch" }

# Credential names are limited to 3-120 chars of alphanumerics and hyphens,
# and the ID-qualified subject is long, so derive a short stable name.
$credName = "gh-$($Repository.Split('/')[-1] -replace '[^a-zA-Z0-9]', '-')-$Branch"
$credName = $credName.Substring(0, [Math]::Min(120, $credName.Length))

$credExists = az ad app federated-credential list --id $appId `
    --query "[?subject=='$subject'] | length(@)" -o tsv

if ($credExists -ne "0") {
    Write-Host "Federated credential for '$subject' already exists." -ForegroundColor DarkGray
} else {
    # Written to a file rather than inlined: quoting a JSON object through
    # PowerShell into az reliably is more trouble than a temp file.
    $params = @{
        name      = $credName
        issuer    = "https://token.actions.githubusercontent.com"
        subject   = $subject
        audiences = @("api://AzureADTokenExchange")
    } | ConvertTo-Json -Compress

    $tmp = New-TemporaryFile
    Set-Content -Path $tmp -Value $params -NoNewline

    Write-Host "Registering federated credential for '$subject' ..."
    az ad app federated-credential create --id $appId --parameters "@$tmp" | Out-Null
    Remove-Item $tmp -Force
}

# ── Role assignments (least privilege, scoped per resource) ──────────────
$scopes = @(
    @{ Role = "Contributor"; Scope = (az acr show -n $AcrName -g $ResourceGroup --query id -o tsv) }
    @{ Role = "Website Contributor"; Scope = (az webapp show -n $AppServiceName -g $ResourceGroup --query id -o tsv) }
    # Every App Service resource needs its own assignment: Website Contributor
    # is scoped to a single site, not to the resource group, so each new app
    # is invisible to CI until it is added here. Both the Function App and the
    # SPA hit this in turn, each surfacing as an AuthorizationFailed on
    # Microsoft.Web/sites/config/list/action at deploy time rather than at
    # apply time.
    @{ Role = "Website Contributor"; Scope = (az functionapp show -n $FunctionAppName -g $ResourceGroup --query id -o tsv) }
    @{ Role = "Website Contributor"; Scope = (az webapp show -n $WebAppName -g $ResourceGroup --query id -o tsv) }
    @{ Role = "SQL Server Contributor"; Scope = (az sql server show -n $SqlServerName -g $ResourceGroup --query id -o tsv) }
    # Upload the Functions deployment package. The runtime storage account has
    # shared_access_key_enabled = false, so this must be a data-plane RBAC
    # grant -- there is no key to fall back on, which is the point.
    @{ Role = "Storage Blob Data Contributor"; Scope = (az storage account show -n $FunctionStorageAccount -g $ResourceGroup --query id -o tsv) }
    # Data-plane access is not enough: the account runs network_rules
    # default_action = Deny, and the deploy job opens and closes a scoped
    # firewall exception for the runner's ephemeral IP around the upload.
    # That is a control-plane operation.
    @{ Role = "Storage Account Contributor"; Scope = (az storage account show -n $FunctionStorageAccount -g $ResourceGroup --query id -o tsv) }
)

foreach ($assignment in $scopes) {
    $already = az role assignment list --assignee $spId --scope $assignment.Scope `
        --query "[?roleDefinitionName=='$($assignment.Role)'] | length(@)" -o tsv

    if ($already -ne "0") {
        Write-Host "$($assignment.Role) already assigned." -ForegroundColor DarkGray
    } else {
        az role assignment create --assignee-object-id $spId --assignee-principal-type ServicePrincipal `
            --role $assignment.Role --scope $assignment.Scope --output none
        Write-Host "Granted $($assignment.Role)." -ForegroundColor Green
    }
}

$tenantId = az account show --query tenantId -o tsv
$subscriptionId = az account show --query id -o tsv

Write-Host ""
Write-Host "Add these as GitHub repository secrets:" -ForegroundColor Cyan
Write-Host "  AZURE_CLIENT_ID       $appId"
Write-Host "  AZURE_TENANT_ID       $tenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID $subscriptionId"
Write-Host ""
Write-Host "Then grant it a database user (as an Entra SQL admin):" -ForegroundColor Cyan
Write-Host "  . .\scripts\dev-db-connect.ps1"
Write-Host "  Invoke-Sqlcmd -ServerInstance $SqlServerName.database.windows.net ``"
Write-Host "    -Database DesiconFinanceWorkflow -AccessToken `$token ``"
Write-Host "    -InputFile .\scripts\create-migration-user.sql ``"
Write-Host "    -Variable @(`"PrincipalName=$DisplayName`") -ErrorAction Stop -Verbose"
