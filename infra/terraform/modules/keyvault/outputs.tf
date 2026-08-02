output "id" {
  description = "Key Vault resource id."
  value       = azurerm_key_vault.this.id
}

output "uri" {
  description = "Key Vault URI, e.g. for KeyVault__Uri app settings."
  value       = azurerm_key_vault.this.vault_uri
}

output "name" {
  description = "Key Vault name."
  value       = azurerm_key_vault.this.name
}
