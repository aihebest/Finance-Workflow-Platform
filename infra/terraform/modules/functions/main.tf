###############################################################################
# Desicon Finance Workflow Platform — Functions module
#
# Linux Elastic Premium Functions app running the three timer-triggered
# sweeps (ReminderSweep, EscalationSweep, RetirementSweep -- see
# docs/02-Solution-Architecture.md §6). VNet-integrated into the same app
# subnet as the API, reached only over Managed Identity for storage/SQL/Key
# Vault, and not fronted by Front Door: nothing about this app is
# user-facing, so public network access is disabled by default.
#
# The Functions runtime storage account is a data service like any other --
# no public network access, no shared key, identity-based
# AzureWebJobsStorage connection, private endpoints into snet-pe.
#
# Usage:
#   module "functions" {
#     source                      = "../../modules/functions"
#     name                        = "func-desicon-fw-dev"
#     storage_account_name        = "stdesiconfwdevfn"
#     resource_group_name         = azurerm_resource_group.main.name
#     location                    = var.location
#     app_subnet_id               = module.network.app_subnet_id
#     pe_subnet_id                = module.network.pe_subnet_id
#     private_dns_zone_ids        = module.network.private_dns_zone_ids
#     key_vault_id                = module.keyvault.id
#     key_vault_uri               = module.keyvault.uri
#     sql_connection_uri          = module.sql.connection_uri
#     app_insights_connection_string = module.monitoring.app_insights_connection_string
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

locals {
  common_app_settings = {
    FUNCTIONS_WORKER_RUNTIME              = "dotnet-isolated"
    WEBSITE_RUN_FROM_PACKAGE              = "1"
    ASPNETCORE_ENVIRONMENT                = var.environment
    KeyVault__Uri                         = var.key_vault_uri
    ApplicationInsights__ConnectionString = var.app_insights_connection_string
    # "WorkflowDb", not "Default" -- see the note in modules/app-service.
    ConnectionStrings__WorkflowDb = var.sql_connection_uri
  }
}

# ── Functions runtime storage: a data service like any other ───────────────
resource "azurerm_storage_account" "functions" {
  name                = "${var.storage_account_name}${var.storage_name_suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = var.storage_replication_type

  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  shared_access_key_enabled       = false # AzureWebJobsStorage connects via storage_uses_managed_identity, not a key.
  public_network_access_enabled   = var.storage_public_network_access_enabled

  network_rules {
    default_action = "Deny"
    bypass         = ["AzureServices"]
    ip_rules       = var.storage_ip_rules
  }

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 30
    }
  }

  tags = var.tags

  # See modules/storage: an unreachable blob data plane hangs against the
  # SDK's default 60-minute create timeout instead of failing fast. This
  # account can also already exist in state from before use_private_endpoints
  # existed (dev's stdesiconfwdevfn did) -- toggling public_network_access_
  # enabled/network_rules on it then runs through the *update* timeout, not
  # create, so both are capped the same way.
  timeouts {
    create = "15m"
    update = "15m"
  }
}

# Skipped entirely when use_private_endpoints is false (dev only): the
# runtime storage account falls back to public access locked down by
# storage_ip_rules instead -- see variables.tf.
resource "azurerm_private_endpoint" "functions_storage" {
  for_each = var.use_private_endpoints ? {
    blob  = { subresource = "blob", zone_key = "blob" }
    queue = { subresource = "queue", zone_key = "queue" }
    table = { subresource = "table", zone_key = "table" }
  } : {}

  name                = "pe-${var.storage_account_name}-${each.key}"
  resource_group_name = var.resource_group_name
  location            = var.location
  subnet_id           = var.pe_subnet_id

  private_service_connection {
    name                           = "psc-${var.storage_account_name}-${each.key}"
    private_connection_resource_id = azurerm_storage_account.functions.id
    subresource_names              = [each.value.subresource]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "default"
    private_dns_zone_ids = [var.private_dns_zone_ids[each.value.zone_key]]
  }

  tags = var.tags
}

resource "azurerm_service_plan" "this" {
  name                = "plan-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.sku_name # Elastic Premium: VNet integration + no cold start for time-sensitive sweeps.

  tags = var.tags
}

resource "azurerm_linux_function_app" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  storage_account_name          = azurerm_storage_account.functions.name
  storage_uses_managed_identity = true

  # ── Hard security posture ────────────────────────────────────────────────
  https_only                                     = true
  public_network_access_enabled                  = var.public_network_access_enabled
  virtual_network_subnet_id                      = var.app_subnet_id
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false
  functions_extension_version                    = "~4"

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on              = true
    ftps_state             = "Disabled"
    minimum_tls_version    = "1.2"
    vnet_route_all_enabled = true

    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "8.0"
    }
  }

  app_settings = merge(local.common_app_settings, var.additional_app_settings)

  tags = var.tags

  lifecycle {
    ignore_changes = [app_settings["WEBSITE_RUN_FROM_PACKAGE"]]
  }
}

# ── Identity-based AzureWebJobsStorage: Blob/Queue/Table data-plane roles ───
# ── Deployment package container ───────────────────────────────────────────
# The Function App runs from a package rather than a Kudu zip deploy.
#
# az functionapp deployment source config-zip cannot be used here: it reaches
# the SCM site with basic authentication, which
# webdeploy_publish_basic_authentication_enabled = false disables. The command
# receives a 401 with an empty body and fails parsing it as JSON, which reads
# as an Azure CLI bug rather than a deliberate lockdown.
#
# Instead CI uploads the published zip here and points
# WEBSITE_RUN_FROM_PACKAGE at the resulting blob URL. The Function App reads
# it with its own managed identity (Storage Blob Data Owner, below), so no
# SAS token, storage key or publishing profile exists anywhere in the
# pipeline. That is also why app_settings["WEBSITE_RUN_FROM_PACKAGE"] is in
# this resource's ignore_changes: the value is owned by the deploy job.
resource "azurerm_storage_container" "deployments" {
  name                  = "deployments"
  storage_account_id    = azurerm_storage_account.functions.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "storage_blob_owner" {
  scope                = azurerm_storage_account.functions.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "storage_queue_contributor" {
  scope                = azurerm_storage_account.functions.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "storage_table_contributor" {
  scope                = azurerm_storage_account.functions.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "kv_secrets_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

# unwrapKey for the Always Encrypted column master key -- see the equivalent
# assignment in modules/app-service. Timer functions that touch Beneficiaries
# need it for exactly the same reason the API does.
resource "azurerm_role_assignment" "kv_crypto_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_linux_function_app.this.identity[0].principal_id
}

# ── Diagnostics ────────────────────────────────────────────────────────────
resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_linux_function_app.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "FunctionAppLogs" }

  enabled_metric { category = "AllMetrics" }
}

resource "azurerm_monitor_diagnostic_setting" "storage" {
  name                       = "diag-${var.storage_account_name}"
  target_resource_id         = "${azurerm_storage_account.functions.id}/blobServices/default"
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "StorageRead" }
  enabled_log { category = "StorageWrite" }
  enabled_log { category = "StorageDelete" }

  enabled_metric { category = "Transaction" }
}
