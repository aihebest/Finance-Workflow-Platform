###############################################################################
# Desicon Finance Workflow Platform — dev environment
#
# Composes every module in infra/terraform/modules/ per
# docs/02-Solution-Architecture.md §5. Same Terraform as uat/prd, different
# .tfvars -- see that doc's "Environments" note.
###############################################################################

terraform {
  required_version = ">= 1.7.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }

  # Partial configuration: values supplied with
  #   terraform init -backend-config=backend.hcl
  # against the storage account created by scripts/bootstrap-state.sh|.ps1.
  # This backend cannot be created by the Terraform that uses it -- see that
  # script's header comment.
  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id

  # Shared keys are disabled on the storage account (shared_access_key_enabled
  # = false in modules/storage) -- without this, the provider's default
  # storage data-plane operations attempt key-based auth first and fail with
  # KeyBasedAuthenticationNotPermitted. This is an auth-mode setting, not a
  # network one: it makes the provider authenticate to blob/queue/table data
  # the same way everything else in this platform does, via Entra ID
  # (Managed Identity / az CLI / SP), which is what RBAC-only actually
  # requires end to end, including from Terraform itself.
  storage_use_azuread = true

  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

data "azurerm_client_config" "current" {}
