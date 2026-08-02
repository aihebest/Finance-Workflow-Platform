###############################################################################
# Desicon Finance Workflow Platform — Monitoring module
#
# Log Analytics Workspace, workspace-based Application Insights, and the
# Action Group every alert in the platform notifies. Every other module's
# `azurerm_monitor_diagnostic_setting` points at this workspace -- "an audit
# trail nobody can query is not an audit trail" (docs/02-Solution-Architecture.md §6).
#
# Metric alerts that target another module's resource (App Service 5xx rate,
# SQL DTU, Function failures) are NOT defined here: this module is a
# dependency of nearly every other module (they all need the workspace id
# and the Application Insights connection string), so an alert referencing
# their resource ids here would create a dependency cycle. Those alerts are
# composed in the environment root instead, against this module's
# `action_group_id` output.
#
# Usage:
#   module "monitoring" {
#     source               = "../../modules/monitoring"
#     name_prefix          = "desicon-fw-dev"
#     resource_group_name  = azurerm_resource_group.main.name
#     location             = var.location
#     alert_email_addresses = ["financeplatform-oncall@desicongroup.com"]
#     tags                 = local.tags
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

resource "azurerm_log_analytics_workspace" "this" {
  name                = "law-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_in_days

  tags = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  application_type    = "web"
  workspace_id        = azurerm_log_analytics_workspace.this.id

  tags = var.tags
}

resource "azurerm_monitor_action_group" "this" {
  name                = "ag-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  short_name          = var.action_group_short_name

  dynamic "email_receiver" {
    for_each = toset(var.alert_email_addresses)
    content {
      name                    = "email-${replace(email_receiver.value, "/[^a-zA-Z0-9]/", "-")}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }

  tags = var.tags
}
