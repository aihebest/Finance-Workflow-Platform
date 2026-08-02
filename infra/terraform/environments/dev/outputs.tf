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
