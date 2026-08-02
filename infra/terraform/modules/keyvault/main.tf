###############################################################################
# Desicon Finance Workflow Platform — Key Vault module
#
# Premium (HSM-backed) vault, RBAC authorization only, purge protection on,
# no public network access. Holds the SQL TDE key, the Storage CMK, and any
# third-party secret (SMTP relay credential, GL connector) the app pulls at
# runtime by Managed Identity -- never a connection string with a password.
#
# Usage:
#   module "keyvault" {
#     source                    = "../../modules/keyvault"
#     name                      = "kv-desicon-fw-dev"
#     resource_group_name       = azurerm_resource_group.main.name
#     location                  = var.location
#     tenant_id                 = var.tenant_id
#     pe_subnet_id              = module.network.pe_subnet_id
#     private_dns_zone_id       = module.network.private_dns_zone_ids["key_vault"]
#     log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
#     administrator_object_ids  = [data.azurerm_client_config.current.object_id]
#     tags                      = local.tags
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

resource "azurerm_key_vault" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = var.tenant_id
  sku_name            = "premium" # HSM-backed keys -- required for the SQL TDE key and Storage CMK.

  # ── Hard security posture ────────────────────────────────────────────────
  rbac_authorization_enabled    = true # Grants show up in the same place as every other Azure permission.
  purge_protection_enabled      = true # Without this a deleted vault takes the SQL TDE key with it.
  soft_delete_retention_days    = var.soft_delete_retention_days
  public_network_access_enabled = var.public_network_access_enabled

  network_acls {
    default_action = "Deny"
    bypass         = "AzureServices"
    ip_rules       = var.ip_rules
  }

  tags = var.tags
}

# ── RBAC: administrators who may manage keys/secrets/certificates ──────────
resource "azurerm_role_assignment" "administrators" {
  for_each             = toset(var.administrator_object_ids)
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Administrator"
  principal_id         = each.value
}

# ── Private endpoint: the only network path to this vault (skipped when
#    use_private_endpoints is false -- dev only, see variables.tf) ──────────
resource "azurerm_private_endpoint" "this" {
  count = var.use_private_endpoints ? 1 : 0

  name                = "pe-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.pe_subnet_id

  private_service_connection {
    name                           = "psc-${var.name}"
    private_connection_resource_id = azurerm_key_vault.this.id
    subresource_names              = ["vault"]
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
  target_resource_id         = azurerm_key_vault.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "AuditEvent" }
  enabled_log { category = "AzurePolicyEvaluationDetails" }

  enabled_metric { category = "AllMetrics" }
}
