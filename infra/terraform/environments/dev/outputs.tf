output "resource_group_name" {
  description = "Resource group containing every resource in this environment."
  value       = azurerm_resource_group.main.name
}

output "app_service_name" {
  description = "App Service name. Its system-assigned identity display name matches this, needed by scripts/create-app-user.ps1|.sh."
  value       = local.app_name
}

output "function_app_name" {
  description = "Function App name. Its system-assigned identity display name matches this, needed by scripts/create-app-user.ps1|.sh."
  value       = module.functions.name
}

output "frontdoor_endpoint_hostname" {
  description = "Public Front Door hostname -- this is the platform's entry point."
  value       = module.frontdoor.endpoint_hostname
}

output "container_registry_name" {
  description = "ACR name, for `az acr login` and the CI push step."
  value       = module.acr.name
}

output "container_registry_login_server" {
  description = "ACR login server (bare host, no scheme) -- the prefix for docker tag/push."
  value       = module.acr.login_server
}

output "sql_server_fqdn" {
  description = "SQL server FQDN, needed by scripts/create-app-user.ps1|.sh."
  value       = module.sql.server_fqdn
}

output "sql_database_name" {
  description = "SQL database name, needed by scripts/create-app-user.ps1|.sh."
  value       = module.sql.database_name
}

output "key_vault_uri" {
  description = "Key Vault URI."
  value       = module.keyvault.uri
}

output "web_app_name" {
  description = "SPA Web App name, for the deploy job."
  value       = module.web_app.name
}

output "web_app_default_hostname" {
  description = "SPA default hostname. Reachable only through Front Door -- direct requests are denied by ip_restriction."
  value       = module.web_app.default_hostname
}
