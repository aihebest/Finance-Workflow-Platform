###############################################################################
# Desicon Finance Workflow Platform — Storage module
#
# Blob storage for expense/advance attachments. Receipts are personal and
# financial data (docs/04-Security-and-DevSecOps.md §2), so this account has
# no public network access, no shared-key auth (Managed Identity + RBAC
# only), a customer-managed key, and an immutable container so an approved
# receipt cannot be silently replaced after the fact.
#
# Usage:
#   module "storage" {
#     source                      = "../../modules/storage"
#     name                        = "stdesiconfwdev"
#     resource_group_name         = azurerm_resource_group.main.name
#     location                    = var.location
#     key_vault_id                = module.keyvault.id
#     pe_subnet_id                = module.network.pe_subnet_id
#     private_dns_zone_id         = module.network.private_dns_zone_ids["blob"]
#     log_analytics_workspace_id  = module.monitoring.log_analytics_workspace_id
#     tags                        = local.tags
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

# ── Identity used solely to wrap/unwrap the storage encryption key ─────────
resource "azurerm_user_assigned_identity" "cmk" {
  name                = "id-${var.name}-cmk"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_role_assignment" "cmk_key_permissions" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Crypto Service Encryption User"
  principal_id         = azurerm_user_assigned_identity.cmk.principal_id
}

resource "azurerm_key_vault_key" "cmk" {
  name         = "cmk-${var.name}"
  key_vault_id = var.key_vault_id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]

  # The identity that will use this key for wrap/unwrap must be able to
  # before the key is referenced by the storage account below; the
  # permission to *create* the key itself comes from administrator_object_ids
  # granted on the keyvault module, not from this role assignment.
  depends_on = [azurerm_role_assignment.cmk_key_permissions]
}

resource "azurerm_storage_account" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = var.replication_type

  # ── Hard security posture ────────────────────────────────────────────────
  min_tls_version                   = "TLS1_2"
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false # Managed Identity + RBAC only -- no account key, ever.
  public_network_access_enabled     = var.public_network_access_enabled
  infrastructure_encryption_enabled = true

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.cmk.id]
  }

  customer_managed_key {
    key_vault_key_id          = azurerm_key_vault_key.cmk.id
    user_assigned_identity_id = azurerm_user_assigned_identity.cmk.id
  }

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
    ip_rules       = var.ip_rules
  }

  blob_properties {
    versioning_enabled  = true # An approved receipt must not be silently replaceable.
    change_feed_enabled = true

    delete_retention_policy {
      days = 30
    }

    container_delete_retention_policy {
      days = 30
    }
  }

  tags = var.tags

  # A storage account with no network path from the deploy agent to the blob
  # data plane doesn't fail fast -- it hangs against the SDK's default
  # 60-minute create timeout. 15 minutes is still generous for a real create
  # and turns a wasted hour into a wasted quarter-hour when public access is
  # misconfigured (or genuinely absent, as in production).
  timeouts {
    create = "15m"
    update = "15m"
  }

  depends_on = [azurerm_role_assignment.cmk_key_permissions]
}

resource "azurerm_storage_container" "attachments" {
  name                  = "attachments"
  storage_account_id    = azurerm_storage_account.this.id
  container_access_type = "private"
}

# Time-based retention (WORM) for the 7-year retention requirement. Left
# unlocked by default -- see var.immutability_locked -- because a locked
# policy can never be removed and blocks account/container deletion
# outright; production sets it true once the container has been verified.
resource "azurerm_storage_container_immutability_policy" "attachments" {
  storage_container_resource_manager_id = azurerm_storage_container.attachments.id
  immutability_period_in_days           = var.immutability_period_in_days
  locked                                = var.immutability_locked
}

# ── Private endpoint: the only network path to this account (skipped when
#    use_private_endpoints is false -- dev only, see variables.tf) ──────────
resource "azurerm_private_endpoint" "this" {
  count = var.use_private_endpoints ? 1 : 0

  name                = "pe-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.pe_subnet_id

  private_service_connection {
    name                           = "psc-${var.name}"
    private_connection_resource_id = azurerm_storage_account.this.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [var.private_dns_zone_id]
  }

  tags = var.tags
}

# ── Diagnostics ────────────────────────────────────────────────────────────
resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = "${azurerm_storage_account.this.id}/blobServices/default"
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "StorageRead" }
  enabled_log { category = "StorageWrite" }
  enabled_log { category = "StorageDelete" }

  enabled_metric { category = "Transaction" }
}
