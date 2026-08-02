output "log_analytics_workspace_id" {
  description = "Log Analytics workspace resource id, for every other module's diagnostic settings."
  value       = azurerm_log_analytics_workspace.this.id
}

output "app_insights_connection_string" {
  description = "Application Insights connection string, for App Service and Functions app settings."
  value       = azurerm_application_insights.this.connection_string
  sensitive   = true
}

output "app_insights_instrumentation_key" {
  description = "Application Insights instrumentation key."
  value       = azurerm_application_insights.this.instrumentation_key
  sensitive   = true
}

output "action_group_id" {
  description = "Action group resource id, for metric alerts composed in the environment root."
  value       = azurerm_monitor_action_group.this.id
}
