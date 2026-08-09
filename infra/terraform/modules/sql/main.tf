###############################################################################
# Desicon Finance Workflow Platform — SQL module
#
# Azure SQL Database, Business Critical, zone redundant, TDE with a
# customer-managed key, Entra-ID-only authentication (no SQL login, no
# password anywhere), no public network access, reached only over a private
# endpoint from the app subnet.
#
# What this module deliberately does NOT do:
#   - Create the contained database user for the app's managed identity
#     (`CREATE USER [app-name] FROM EXTERNAL PROVIDER`). That statement must
#     run as an Entra admin against the live database -- Terraform's ARM
#     provider has no data-plane SQL access. Run scripts/create-app-user.sql
#     (via scripts/create-app-user.ps1 or .sh) once, after apply.
#   - Provision the Always Encrypted column master/encryption keys for
#     Beneficiary.BankAccountNumber. See scripts/Provision-AlwaysEncryptedKeys.ps1.
#
# Usage:
#   module "sql" {
#     source                      = "../../modules/sql"
#     name                        = "sql-desicon-fw-dev"
#     database_name               = "DesiconFinanceWorkflow"
#     resource_group_name         = azurerm_resource_group.main.name
#     location                    = var.location
#     key_vault_id                = module.keyvault.id
#     pe_subnet_id                = module.network.pe_subnet_id
#     private_dns_zone_id         = module.network.private_dns_zone_ids["sql"]
#     log_analytics_workspace_id  = module.monitoring.log_analytics_workspace_id
#     entra_admin_login           = "sql-admins-dev"
#     entra_admin_object_id       = var.entra_sql_admin_group_object_id
#     zone_redundant              = false
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

# ── Identity used solely to wrap/unwrap the TDE key ─────────────────────────
resource "azurerm_user_assigned_identity" "tde" {
  name                = "id-${var.name}-tde"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}

resource "azurerm_role_assignment" "tde_key_permissions" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Crypto Service Encryption User"
  principal_id         = azurerm_user_assigned_identity.tde.principal_id
}

resource "azurerm_key_vault_key" "tde" {
  name         = "cmk-${var.name}-tde"
  key_vault_id = var.key_vault_id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey"]

  depends_on = [azurerm_role_assignment.tde_key_permissions]
}

resource "azurerm_mssql_server" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  version             = "12.0"
  minimum_tls_version = "1.2"

  # ── Hard security posture ────────────────────────────────────────────────
  public_network_access_enabled        = var.public_network_access_enabled
  outbound_network_restriction_enabled = true

  # Stated explicitly because the provider's default is false and Azure has
  # this enabled today. Left unset, the next apply would have turned SQL
  # vulnerability assessment OFF as a side effect of an unrelated change --
  # found in a plan on 9 Aug 2026 that was only meant to add two app settings.
  #
  # A security control that switches itself off during a routine deployment,
  # with the change buried in a four-item plan nobody reads closely, is the
  # same failure this project keeps finding: silent, and invisible afterwards.
  express_vulnerability_assessment_enabled = true

  # No SQL login exists at all -- Entra ID is the only way in.
  azuread_administrator {
    login_username              = var.entra_admin_login
    object_id                   = var.entra_admin_object_id
    azuread_authentication_only = true
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.tde.id]
  }

  primary_user_assigned_identity_id            = azurerm_user_assigned_identity.tde.id
  transparent_data_encryption_key_vault_key_id = azurerm_key_vault_key.tde.id

  tags = var.tags
}

resource "azurerm_mssql_database" "this" {
  name      = var.database_name
  server_id = azurerm_mssql_server.this.id

  sku_name       = var.sku_name # Business Critical: BC_Gen5_N.
  zone_redundant = var.zone_redundant

  storage_account_type = "Geo" # Backups geo-replicated to the paired region.

  short_term_retention_policy {
    retention_days = 35
  }

  long_term_retention_policy {
    weekly_retention = "P7Y"
  }

  tags = var.tags
}

# ── Firewall rules: the dev-only alternative to the private endpoint below.
#    SQL has no network_acls-style inline block -- each allowed address is
#    its own azurerm_mssql_firewall_rule. Empty when use_private_endpoints
#    is true, since the private endpoint is the only path in that case. ────
resource "azurerm_mssql_firewall_rule" "deployer" {
  for_each = var.use_private_endpoints ? toset([]) : toset(var.ip_rules)

  name             = "deployer-${replace(each.value, ".", "-")}"
  server_id        = azurerm_mssql_server.this.id
  start_ip_address = each.value
  end_ip_address   = each.value
}

# ── VNet rules: how the application reaches SQL when there is no private
#    endpoint. The firewall rules above admit the *deployer*, which is a
#    developer laptop or a CI runner -- not the App Service, whose outbound
#    traffic leaves through the integrated app subnet with a private source
#    address that no ip_rules entry can ever match.
#
#    Without this the app starts cleanly, answers /health/live, and fails
#    every database call. The failure is a connection timeout, so it reads
#    as "SQL is down" rather than "this subnet was never admitted".
#
#    Requires the Microsoft.Sql service endpoint on each subnet -- see
#    modules/network. A rule naming a subnet without that endpoint is
#    accepted and silently never matches. ──────────────────────────────────
resource "azurerm_mssql_virtual_network_rule" "app" {
  for_each = var.use_private_endpoints ? toset([]) : toset(var.vnet_rule_subnet_ids)

  name      = "vnet-${substr(md5(each.value), 0, 8)}"
  server_id = azurerm_mssql_server.this.id
  subnet_id = each.value
}

# ── Private endpoint: the only network path to this server (skipped when
#    use_private_endpoints is false -- dev only, see variables.tf) ─────────
resource "azurerm_private_endpoint" "this" {
  count = var.use_private_endpoints ? 1 : 0

  name                = "pe-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.pe_subnet_id

  private_service_connection {
    name                           = "psc-${var.name}"
    private_connection_resource_id = azurerm_mssql_server.this.id
    subresource_names              = ["sqlServer"]
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
  name                       = "diag-${var.database_name}"
  target_resource_id         = azurerm_mssql_database.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "SQLInsights" }
  enabled_log { category = "AutomaticTuning" }
  enabled_log { category = "Errors" }
  enabled_log { category = "DatabaseWaitStatistics" }
  enabled_log { category = "Timeouts" }
  enabled_log { category = "Blocks" }
  enabled_log { category = "Deadlocks" }

  enabled_metric { category = "Basic" }
  enabled_metric { category = "InstanceAndAppAdvanced" }
}
