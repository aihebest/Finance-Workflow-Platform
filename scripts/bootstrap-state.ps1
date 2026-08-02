<#
.SYNOPSIS
    Creates the resource group, storage account and blob container that hold
    Terraform state for infra/terraform/environments/*.

.DESCRIPTION
    One-time, per-subscription bootstrap step. It must run BEFORE
    `terraform init` in any environment, because the azurerm backend cannot
    create the storage account it authenticates against.

    This account is intentionally outside the "all data services disabled
    for public access" rule that governs Terraform-managed resources
    (policy/terraform/azure_security.rego): CI runners need to reach it over
    the internet to run plan/apply, and it is not managed by the Terraform
    it backs. It is still locked down independently: TLS 1.2 minimum,
    versioned blobs, soft delete, and RBAC-only access (no shared keys).

    Re-running this script is safe: every create step is skipped if the
    resource already exists.

    Requires the Azure CLI (az), logged in (Connect-AzAccount or az login)
    with Contributor + User Access Administrator on the target subscription.

.PARAMETER ResourceGroupName
    Resource group to hold the state storage account.

.PARAMETER StorageAccountName
    Globally unique storage account name, lowercase alphanumeric, 3-24 chars.

.PARAMETER ContainerName
    Blob container that holds the .tfstate files, one per environment.

.PARAMETER Location
    Azure region.

.EXAMPLE
    ./bootstrap-state.ps1
    Uses all defaults.

.EXAMPLE
    ./bootstrap-state.ps1 -StorageAccountName "sttfstatedesiconfw2"
#>
[CmdletBinding()]
param(
    [string]$ResourceGroupName = "rg-desicon-fw-tfstate",
    [string]$StorageAccountName = "sttfstatedesiconfw",
    [string]$ContainerName = "tfstate",
    [string]$Location = "southafricanorth"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) is required but was not found on PATH."
}

try {
    az account show *> $null
} catch {
    throw "Not logged in. Run 'az login' first."
}

$subscriptionName = az account show --query name -o tsv
Write-Host "Subscription:     $subscriptionName"
Write-Host "Resource group:   $ResourceGroupName"
Write-Host "Storage account:  $StorageAccountName"
Write-Host "Container:        $ContainerName"
Write-Host "Location:         $Location"
Write-Host ""

$rgExists = az group show --name $ResourceGroupName *>$null; $rgFound = $?
if ($rgFound) {
    Write-Host "Resource group $ResourceGroupName already exists, skipping."
} else {
    Write-Host "Creating resource group $ResourceGroupName..."
    az group create --name $ResourceGroupName --location $Location `
        --tags environment=shared owner=platform-engineering purpose=terraform-state `
        *> $null
}

az storage account show --name $StorageAccountName --resource-group $ResourceGroupName *> $null
if ($?) {
    Write-Host "Storage account $StorageAccountName already exists, skipping create."
} else {
    Write-Host "Creating storage account $StorageAccountName..."
    az storage account create `
        --name $StorageAccountName `
        --resource-group $ResourceGroupName `
        --location $Location `
        --sku Standard_GRS `
        --kind StorageV2 `
        --min-tls-version TLS1_2 `
        --allow-blob-public-access false `
        --allow-shared-key-access false `
        --https-only true `
        --tags environment=shared owner=platform-engineering purpose=terraform-state `
        *> $null
}

Write-Host "Enabling blob versioning and soft delete..."
az storage account blob-service-properties update `
    --account-name $StorageAccountName `
    --resource-group $ResourceGroupName `
    --enable-versioning true `
    --enable-delete-retention true `
    --delete-retention-days 30 `
    --enable-container-delete-retention true `
    --container-delete-retention-days 30 `
    *> $null

# Subscription Owner (or even User Access Administrator) does not imply
# blob data access. ARM control-plane roles and Storage's own data-plane
# roles are separate RBAC planes -- shared keys are disabled on this
# account (--allow-shared-key-access false above), so "Storage Blob Data
# Contributor" is the *only* way to read or write state, regardless of
# what control-plane role the caller holds. The CI service principal that
# runs `terraform init`/`plan`/`apply` against this backend needs this
# same role granted on this same scope, or its init will fail with an
# authorization error even though it can see and manage the account
# itself. Grant it once, e.g.:
#   az role assignment create --assignee <ci-sp-object-id> `
#     --assignee-principal-type ServicePrincipal `
#     --role "Storage Blob Data Contributor" --scope $storageId
$callerObjectId = az ad signed-in-user show --query id -o tsv
$storageId = az storage account show --name $StorageAccountName --resource-group $ResourceGroupName --query id -o tsv

Write-Host "Granting caller 'Storage Blob Data Contributor' (RBAC-only access, shared keys are disabled)..."
try {
    az role assignment create `
        --assignee-object-id $callerObjectId `
        --assignee-principal-type User `
        --role "Storage Blob Data Contributor" `
        --scope $storageId `
        *> $null
} catch {
    Write-Host "  (role assignment already exists or requires elevated privileges -- verify manually)"
}

az storage container show --name $ContainerName --account-name $StorageAccountName --auth-mode login *> $null
if ($?) {
    Write-Host "Container $ContainerName already exists, skipping."
} else {
    Write-Host "Creating container $ContainerName..."
    az storage container create `
        --name $ContainerName `
        --account-name $StorageAccountName `
        --auth-mode login `
        *> $null
}

Write-Host ""
Write-Host "Bootstrap complete. Use this backend configuration:"
Write-Host ""
Write-Host "  resource_group_name  = `"$ResourceGroupName`""
Write-Host "  storage_account_name = `"$StorageAccountName`""
Write-Host "  container_name        = `"$ContainerName`""
Write-Host "  key                    = `"<environment>.terraform.tfstate`""
Write-Host ""
Write-Host "Copy infra/terraform/environments/<env>/backend.hcl.example to backend.hcl"
Write-Host "with these values, then run:"
Write-Host ""
Write-Host "  terraform init -backend-config=backend.hcl"
