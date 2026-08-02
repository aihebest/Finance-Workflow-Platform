output "id" {
  description = "Function App resource id."
  value       = azurerm_linux_function_app.this.id
}

output "name" {
  description = "Function App name."
  value       = azurerm_linux_function_app.this.name
}

output "principal_id" {
  description = "System-assigned managed identity principal id, for granting access to SQL."
  value       = azurerm_linux_function_app.this.identity[0].principal_id
}

output "default_hostname" {
  description = "Default hostname of the Function App."
  value       = azurerm_linux_function_app.this.default_hostname
}

output "service_plan_id" {
  description = "Function App Service Plan resource id."
  value       = azurerm_service_plan.this.id
}
