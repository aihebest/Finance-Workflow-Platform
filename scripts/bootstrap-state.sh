#!/usr/bin/env bash
#
# Creates the resource group, storage account and blob container that hold
# Terraform state for infra/terraform/environments/*. This is a one-time,
# per-subscription bootstrap step -- it must run BEFORE `terraform init` in
# any environment, because the azurerm backend cannot create the storage
# account it authenticates against.
#
# This account is intentionally outside the "all data services disabled for
# public access" rule that governs Terraform-managed resources
# (policy/terraform/azure_security.rego): CI runners need to reach it over
# the internet to run plan/apply, and it is not managed by the Terraform it
# backs. It is still locked down independently: TLS 1.2 minimum, versioned
# blobs, soft delete, and RBAC-only access (no shared keys).
#
# Usage:
#   ./scripts/bootstrap-state.sh [environment]
#
# Environment variables (all optional, shown with defaults):
#   RESOURCE_GROUP        rg-desicon-fw-tfstate
#   STORAGE_ACCOUNT       sttfstatedesiconfw
#   CONTAINER_NAME         tfstate
#   LOCATION               southafricanorth
#
# Requires: az CLI, logged in (`az login`) with Contributor + User Access
# Administrator on the target subscription.

set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-desicon-fw-tfstate}"
STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-sttfstatedesiconfw}"
CONTAINER_NAME="${CONTAINER_NAME:-tfstate}"
LOCATION="${LOCATION:-southafricanorth}"

if ! command -v az >/dev/null 2>&1; then
  echo "ERROR: Azure CLI (az) is required but was not found on PATH." >&2
  exit 1
fi

az account show >/dev/null 2>&1 || {
  echo "ERROR: not logged in. Run 'az login' first." >&2
  exit 1
}

echo "Subscription: $(az account show --query name -o tsv)"
echo "Resource group:   ${RESOURCE_GROUP}"
echo "Storage account:  ${STORAGE_ACCOUNT}"
echo "Container:        ${CONTAINER_NAME}"
echo "Location:         ${LOCATION}"
echo

if az group show --name "${RESOURCE_GROUP}" >/dev/null 2>&1; then
  echo "Resource group ${RESOURCE_GROUP} already exists, skipping."
else
  echo "Creating resource group ${RESOURCE_GROUP}..."
  az group create --name "${RESOURCE_GROUP}" --location "${LOCATION}" \
    --tags environment=shared owner=platform-engineering purpose=terraform-state \
    >/dev/null
fi

if az storage account show --name "${STORAGE_ACCOUNT}" --resource-group "${RESOURCE_GROUP}" >/dev/null 2>&1; then
  echo "Storage account ${STORAGE_ACCOUNT} already exists, skipping create."
else
  echo "Creating storage account ${STORAGE_ACCOUNT}..."
  az storage account create \
    --name "${STORAGE_ACCOUNT}" \
    --resource-group "${RESOURCE_GROUP}" \
    --location "${LOCATION}" \
    --sku Standard_GRS \
    --kind StorageV2 \
    --min-tls-version TLS1_2 \
    --allow-blob-public-access false \
    --allow-shared-key-access false \
    --https-only true \
    --tags environment=shared owner=platform-engineering purpose=terraform-state \
    >/dev/null
fi

echo "Enabling blob versioning and soft delete..."
az storage account blob-service-properties update \
  --account-name "${STORAGE_ACCOUNT}" \
  --resource-group "${RESOURCE_GROUP}" \
  --enable-versioning true \
  --enable-delete-retention true \
  --delete-retention-days 30 \
  --enable-container-delete-retention true \
  --container-delete-retention-days 30 \
  >/dev/null

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
#   az role assignment create --assignee <ci-sp-object-id> \
#     --assignee-principal-type ServicePrincipal \
#     --role "Storage Blob Data Contributor" --scope "${STORAGE_ID}"
CALLER_OBJECT_ID="$(az ad signed-in-user show --query id -o tsv)"
STORAGE_ID="$(az storage account show --name "${STORAGE_ACCOUNT}" --resource-group "${RESOURCE_GROUP}" --query id -o tsv)"

echo "Granting caller 'Storage Blob Data Contributor' (RBAC-only access, shared keys are disabled)..."
az role assignment create \
  --assignee-object-id "${CALLER_OBJECT_ID}" \
  --assignee-principal-type User \
  --role "Storage Blob Data Contributor" \
  --scope "${STORAGE_ID}" \
  >/dev/null 2>&1 || echo "  (role assignment already exists or requires elevated privileges -- verify manually)"

if az storage container show --name "${CONTAINER_NAME}" --account-name "${STORAGE_ACCOUNT}" --auth-mode login >/dev/null 2>&1; then
  echo "Container ${CONTAINER_NAME} already exists, skipping."
else
  echo "Creating container ${CONTAINER_NAME}..."
  az storage container create \
    --name "${CONTAINER_NAME}" \
    --account-name "${STORAGE_ACCOUNT}" \
    --auth-mode login \
    >/dev/null
fi

cat <<EOF

Bootstrap complete. Use this backend configuration:

  resource_group_name  = "${RESOURCE_GROUP}"
  storage_account_name = "${STORAGE_ACCOUNT}"
  container_name        = "${CONTAINER_NAME}"
  key                    = "<environment>.terraform.tfstate"

Copy infra/terraform/environments/<env>/backend.hcl.example to backend.hcl
with these values, then run:

  terraform init -backend-config=backend.hcl
EOF
