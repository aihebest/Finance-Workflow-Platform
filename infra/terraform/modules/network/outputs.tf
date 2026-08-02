output "vnet_id" {
  description = "Virtual network resource id."
  value       = azurerm_virtual_network.this.id
}

output "app_subnet_id" {
  description = "snet-app resource id, for App Service / Functions VNet integration."
  value       = azurerm_subnet.app.id
}

output "pe_subnet_id" {
  description = "snet-pe resource id, for private endpoints."
  value       = azurerm_subnet.pe.id
}

output "private_dns_zone_ids" {
  description = "Map of private DNS zone resource ids, keyed by service (sql, key_vault, blob)."
  value       = { for k, z in azurerm_private_dns_zone.this : k => z.id }
}

output "private_dns_zone_names" {
  description = "Map of private DNS zone names, keyed by service (sql, key_vault, blob)."
  value       = { for k, z in azurerm_private_dns_zone.this : k => z.name }
}
