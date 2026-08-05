output "id" {
  description = "Web App resource id."
  value       = azurerm_linux_web_app.this.id
}

output "name" {
  description = "Web App name."
  value       = azurerm_linux_web_app.this.name
}

output "default_hostname" {
  description = "Default hostname, used as the Front Door origin."
  value       = azurerm_linux_web_app.this.default_hostname
}
