output "id" {
  description = "Registry resource id, for the AcrPull role assignment on the app's managed identity."
  value       = azurerm_container_registry.this.id
}

output "name" {
  description = "Registry name, e.g. crdesiconfwdev."
  value       = azurerm_container_registry.this.name
}

output "login_server" {
  description = "Registry login server, e.g. crdesiconfwdev.azurecr.io. Bare host, no scheme."
  value       = azurerm_container_registry.this.login_server
}

output "registry_url" {
  description = "Registry login server with the https:// scheme that azurerm_linux_web_app's docker_registry_url expects. Kept distinct from login_server because the CI docker login and the App Service setting want different forms of the same value, and passing the wrong one produces a pull failure that reads as an authentication error."
  value       = "https://${azurerm_container_registry.this.login_server}"
}
