###############################################################################
# Desicon Finance Workflow Platform — SPA hosting
#
# nginx serving a compiled Vite bundle from a container, behind the same Front
# Door as the API.
#
# WHY NOT modules/app-service
# ---------------------------
# That module is the API: it carries auth_settings_v2, a SQL connection
# string, Key Vault and Storage role assignments, and Easy Auth returning 401
# to anything unauthenticated. Every one of those is wrong here. A SPA must be
# served anonymously — the browser has to fetch index.html and the JavaScript
# bundle *before* it can sign anybody in, and Easy Auth in front of static
# files would return 401 to the very request that loads MSAL.
#
# Authentication for the SPA happens in the browser against Entra, and the
# access token it obtains is what the API validates. The static files
# themselves are not a secret: they are the same bundle every user downloads,
# and they contain no credential — the Entra client id is public by design.
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

resource "azurerm_service_plan" "this" {
  name                = "plan-${var.name}"
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = "Linux"
  sku_name            = var.sku_name

  tags = var.tags
}

resource "azurerm_linux_web_app" "this" {
  name                = var.name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = azurerm_service_plan.this.id

  https_only = true

  # Managed identity solely to pull from ACR. This app reads no data, holds no
  # secret and needs no other grant.
  identity {
    type = "SystemAssigned"
  }

  ftp_publish_basic_authentication_enabled       = false
  webdeploy_publish_basic_authentication_enabled = false

  site_config {
    always_on                         = var.always_on
    ftps_state                        = "Disabled"
    http2_enabled                     = true
    minimum_tls_version               = "1.2"
    remote_debugging_enabled          = false
    health_check_path                 = "/healthz"
    health_check_eviction_time_in_min = 5

    # See modules/app-service: leaving the registry credentials null selects
    # an anonymous pull, not a managed-identity one. The flag is what makes
    # the AcrPull assignment below actually do anything.
    container_registry_use_managed_identity = true

    application_stack {
      docker_image_name        = var.container_image
      docker_registry_url      = var.container_registry_url
      docker_registry_username = null
      docker_registry_password = null
    }

    ip_restriction_default_action = "Deny"

    # Only Front Door may reach the app directly, so the WAF cannot be
    # bypassed by hitting the azurewebsites.net hostname.
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
  }

  app_settings = {
    WEBSITES_ENABLE_APP_SERVICE_STORAGE = "false"
    WEBSITES_PORT                       = "8080" # docker/nginx.conf listens here, and nginx runs unprivileged so it cannot bind 80.
  }

  tags = var.tags

  lifecycle {
    # The deploy job owns the image tag, exactly as it does for the API.
    ignore_changes = [site_config[0].application_stack[0].docker_image_name]
  }
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = var.container_registry_id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_linux_web_app.this.identity[0].principal_id
}

resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-${var.name}"
  target_resource_id         = azurerm_linux_web_app.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "AppServiceHTTPLogs" }
  enabled_log { category = "AppServicePlatformLogs" }

  enabled_metric { category = "AllMetrics" }
}
