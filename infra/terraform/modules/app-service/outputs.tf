output "id" {
  description = "App Service resource id."
  value       = azurerm_linux_web_app.this.id
}

output "default_hostname" {
  description = "Default hostname of the app."
  value       = azurerm_linux_web_app.this.default_hostname
}

output "principal_id" {
  description = "System-assigned managed identity principal id, for granting access to SQL, Key Vault and Storage."
  value       = azurerm_linux_web_app.this.identity[0].principal_id
}

output "staging_principal_id" {
  description = "Managed identity principal id of the staging slot, if created."
  value       = var.enable_staging_slot ? azurerm_linux_web_app_slot.staging[0].identity[0].principal_id : null
}

output "service_plan_id" {
  description = "App Service Plan resource id."
  value       = azurerm_service_plan.this.id
}
