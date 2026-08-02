output "id" {
  description = "Storage account resource id, for the app-service module's Storage Blob Data Contributor role assignment."
  value       = azurerm_storage_account.this.id
}

output "name" {
  description = "Storage account name."
  value       = azurerm_storage_account.this.name
}

output "primary_blob_endpoint" {
  description = "Primary blob service endpoint."
  value       = azurerm_storage_account.this.primary_blob_endpoint
}

output "attachments_container_name" {
  description = "Name of the immutable attachments container."
  value       = azurerm_storage_container.attachments.name
}
