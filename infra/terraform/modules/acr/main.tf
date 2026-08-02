###############################################################################
# Desicon Finance Workflow Platform — Container Registry module
#
# Holds the API container image. Admin user is disabled unconditionally --
# policy/terraform/azure_security.rego denies admin_enabled == true outright,
# on the reasoning that image pull is the app's managed identity via AcrPull
# and CI's push is Entra via OIDC, so the admin credential exists only to be
# leaked.
#
# This registry is the reason the platform can stay credential-free end to
# end. The alternative considered (GitHub Container Registry) works, but a
# private GHCR package requires App Service to hold a long-lived PAT as a
# registry password -- which would have been the only stored credential in a
# system that authenticates to SQL, Storage and Key Vault by managed identity.
#
# Usage:
#   module "acr" {
#     source                     = "../../modules/acr"
#     name                       = "crdesiconfwdev"
#     resource_group_name        = azurerm_resource_group.main.name
#     location                   = var.location
#     log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
#     tags                       = local.tags
#   }
###############################################################################

terraform {
  required_version = ">= 1.7.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

resource "azurerm_container_registry" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = var.sku

  # ── Hard security posture ────────────────────────────────────────────────
  # Never true. See the deny rule in policy/terraform/azure_security.rego.
  admin_enabled = false

  # Basic and Standard do not support network rules or private endpoints at
  # all; the argument is only meaningful on Premium. Dev runs Basic and
  # accepts public reachability of the registry -- the images are pulled by
  # an identity that must still hold AcrPull, so public network access is not
  # public read.
  public_network_access_enabled = var.public_network_access_enabled

  # Premium-only, and null (not 0) on lower tiers -- azurerm 4.x takes this as
  # a plain attribute, not the retention_policy block that 3.x used. Guarded
  # rather than hardcoded so uat/prd can raise the sku without forking this.
  retention_policy_in_days = var.sku == "Premium" ? var.untagged_retention_days : null

  tags = var.tags
}

# ── Diagnostics ────────────────────────────────────────────────────────────
# ContainerRegistryLoginEvents is the one that matters for audit: it records
# every authentication against the registry, which is how an unexpected pull
# identity becomes visible.
resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_container_registry.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "ContainerRegistryLoginEvents" }
  enabled_log { category = "ContainerRegistryRepositoryEvents" }

  enabled_metric { category = "AllMetrics" }
}
