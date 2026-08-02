###############################################################################
# Desicon Finance Workflow Platform — App Service module
#
# Hardened Linux container App Service with VNet integration, Managed Identity,
# and no public data-plane access to backing services.
#
# Usage:
#   module "api" {
#     source              = "../../modules/app-service"
#     name                = "app-desicon-fw-api-prd"
#     resource_group_name = azurerm_resource_group.main.name
#     location            = var.location
#     subnet_id           = module.network.app_subnet_id
#     key_vault_id        = module.keyvault.id
#     sql_connection_uri  = module.sql.connection_uri
#     app_insights_key    = module.monitoring.connection_string
#     container_image     = "${module.acr.login_server}/desicon-api:${var.image_tag}"
#     tags                = local.tags
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
  # Only these headers/settings are environment-specific; everything security
  # related is fixed here so an environment cannot opt out of a control.
  common_app_settings = {
    WEBSITES_ENABLE_APP_SERVICE_STORAGE   = "false"
    WEBSITE_RUN_FROM_PACKAGE              = "0"
    ASPNETCORE_ENVIRONMENT                = var.environment
    ASPNETCORE_FORWARDEDHEADERS_ENABLED   = "true"
    KeyVault__Uri                         = var.key_vault_uri
    ApplicationInsights__ConnectionString = var.app_insights_connection_string
    # Connection string carries no credential — Managed Identity authenticates.
    ConnectionStrings__Default = var.sql_connection_uri
  }
}

resource "azurerm_service_plan" "this" {
  name                   = "plan-${var.name}"
  resource_group_name    = var.resource_group_name
  location               = var.location
  os_type                = "Linux"
  sku_name               = var.sku_name
  zone_balancing_enabled = var.zone_redundant

  tags = var.tags
}

resource "azurerm_linux_web_app" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  # ── Hard security posture ────────────────────────────────────────────────
  https_only                                     = true
  public_network_access_enabled                  = var.public_network_access_enabled
  virtual_network_subnet_id                      = var.subnet_id
  client_certificate_enabled                     = false
  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                         = true
    ftps_state                        = "Disabled"
    http2_enabled                     = true
    minimum_tls_version               = "1.2"
    remote_debugging_enabled          = false
    vnet_route_all_enabled            = true
    health_check_path                 = "/health/ready"
    health_check_eviction_time_in_min = 5
    worker_count                      = var.worker_count

    application_stack {
      docker_image_name   = var.container_image
      docker_registry_url = var.container_registry_url
      # Registry pull uses the app's managed identity — no admin credentials.
      docker_registry_username = null
      docker_registry_password = null
    }

    ip_restriction_default_action = "Deny"

    # Only Front Door may reach the app directly.
    dynamic "ip_restriction" {
      for_each = var.front_door_id == null ? [] : [1]
      content {
        name        = "AllowFrontDoorOnly"
        priority    = 100
        action      = "Allow"
        service_tag = "AzureFrontDoor.Backend"
        headers {
          x_azure_fdid = [var.front_door_id]
        }
      }
    }

    # provider 4.81 rejects allowed_origins with an empty list -- omit the
    # block entirely rather than send cors { allowed_origins = [] } when no
    # frontend origin has been configured yet.
    dynamic "cors" {
      for_each = length(var.allowed_cors_origins) > 0 ? [1] : []
      content {
        allowed_origins     = var.allowed_cors_origins
        support_credentials = true
      }
    }
  }

  app_settings = merge(local.common_app_settings, var.additional_app_settings)

  auth_settings_v2 {
    auth_enabled           = true
    require_authentication = true
    unauthenticated_action = "Return401"
    default_provider       = "azureactivedirectory"

    active_directory_v2 {
      client_id                  = var.entra_client_id
      tenant_auth_endpoint       = "https://login.microsoftonline.com/${var.tenant_id}/v2.0"
      allowed_audiences          = ["api://${var.entra_client_id}"]
      client_secret_setting_name = null # Managed Identity federated credential
    }

    login {
      token_store_enabled = false
    }
  }

  logs {
    detailed_error_messages = false
    failed_request_tracing  = false

    http_logs {
      file_system {
        retention_in_days = 7
        retention_in_mb   = 35
      }
    }

    application_logs {
      file_system_level = "Warning"
    }
  }

  sticky_settings {
    app_setting_names = ["ASPNETCORE_ENVIRONMENT"]
  }

  tags = var.tags

  lifecycle {
    # Image tag is advanced by the deploy pipeline, not by terraform apply.
    ignore_changes = [site_config[0].application_stack[0].docker_image_name]
  }
}

# Staging slot — deployments land here, are smoke-tested, then swapped.
resource "azurerm_linux_web_app_slot" "staging" {
  count          = var.enable_staging_slot ? 1 : 0
  name           = "staging"
  app_service_id = azurerm_linux_web_app.this.id

  https_only                = true
  virtual_network_subnet_id = var.subnet_id

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                         = true
    ftps_state                        = "Disabled"
    minimum_tls_version               = "1.2"
    health_check_path                 = "/health/ready"
    health_check_eviction_time_in_min = 5
    vnet_route_all_enabled            = true

    application_stack {
      docker_image_name   = var.container_image
      docker_registry_url = var.container_registry_url
    }
  }

  app_settings = merge(local.common_app_settings, var.additional_app_settings)

  tags = var.tags
}

# ── Key Vault access via Managed Identity (RBAC, not access policies) ───────
# for_each over a static set gated by a plain bool, not `count` keyed off a
# value's nullness -- see the enable_* variable block in variables.tf for why.
resource "azurerm_role_assignment" "kv_secrets_user" {
  for_each = var.enable_keyvault_role_assignment ? toset(["this"]) : toset([])

  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "kv_secrets_user_slot" {
  count                = var.enable_staging_slot ? 1 : 0
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app_slot.staging[0].identity[0].principal_id
}

resource "azurerm_role_assignment" "storage_blob_contributor" {
  for_each = var.enable_storage_role_assignment ? toset(["this"]) : toset([])

  scope                = var.storage_account_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_web_app.this.identity[0].principal_id
}

resource "azurerm_role_assignment" "acr_pull" {
  for_each = var.enable_acr_role_assignment ? toset(["this"]) : toset([])

  scope                = var.container_registry_id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_linux_web_app.this.identity[0].principal_id
}

# ── Diagnostics ────────────────────────────────────────────────────────────
resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_linux_web_app.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "AppServiceHTTPLogs" }
  enabled_log { category = "AppServiceConsoleLogs" }
  enabled_log { category = "AppServiceAppLogs" }
  enabled_log { category = "AppServiceAuditLogs" }
  enabled_log { category = "AppServiceIPSecAuditLogs" }
  enabled_log { category = "AppServicePlatformLogs" }

  enabled_metric { category = "AllMetrics" }
}
