variable "name" {
  description = "App Service name, e.g. app-desicon-fw-api-prd."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region. South Africa North is the closest region to Lagos with a paired region."
  type        = string
}

variable "environment" {
  description = "dev, uat or prd."
  type        = string

  validation {
    condition     = contains(["dev", "uat", "prd"], var.environment)
    error_message = "Environment must be one of: dev, uat, prd."
  }
}

variable "sku_name" {
  description = "App Service Plan SKU."
  type        = string
  default     = "P1v3"
}

variable "zone_redundant" {
  description = "Spread instances across availability zones. Required in prd."
  type        = bool
  default     = false
}

variable "worker_count" {
  description = "Instance count."
  type        = number
  default     = 2
}

variable "subnet_id" {
  description = "Subnet for VNet integration. All egress to backing services routes through here."
  type        = string
}

variable "public_network_access_enabled" {
  description = "Whether the app accepts traffic outside the VNet. Keep true only where Front Door fronts the app; backing services are private regardless."
  type        = bool
  default     = true
}

variable "front_door_id" {
  description = "Front Door resource GUID. When set, only Front Door may reach the app."
  type        = string
  default     = null
}

variable "container_image" {
  description = "Fully qualified container image, including tag."
  type        = string
}

variable "container_registry_url" {
  description = "Registry login server URL."
  type        = string
}

variable "container_registry_id" {
  description = "Registry resource id, for the AcrPull role assignment."
  type        = string
  default     = null
}

variable "key_vault_id" {
  description = "Key Vault resource id, for the Secrets User role assignment."
  type        = string
}

# ── Role-assignment toggles ─────────────────────────────────────────────────
# Whether each of these role assignments exists is decided by these explicit
# booleans, not by inferring it from container_registry_id/storage_account_id
# being null. Those ids are typically another module's output (e.g.
# module.storage.id) and are not known until apply -- a `count` or `for_each`
# keyed off `var.x == null` fails plan with "Invalid count argument" the
# moment the id stops being a plan-time-known literal. A for_each keyed off a
# plain bool has no such requirement. The environment composition must set
# these explicitly alongside the corresponding *_id variable.

variable "enable_keyvault_role_assignment" {
  description = "Grant the app's managed identity 'Key Vault Secrets User' on key_vault_id. key_vault_id is a required argument, so this should normally stay true; the toggle exists for symmetry with enable_storage_role_assignment/enable_acr_role_assignment."
  type        = bool
  default     = true
}

variable "enable_storage_role_assignment" {
  description = "Grant the app's managed identity 'Storage Blob Data Contributor' on storage_account_id. Must be false if storage_account_id is not supplied."
  type        = bool
  default     = true
}

variable "enable_acr_role_assignment" {
  description = "Grant the app's managed identity 'AcrPull' on container_registry_id. Must be false if container_registry_id is not supplied."
  type        = bool
  default     = true
}

variable "key_vault_uri" {
  description = "Key Vault URI passed to the app."
  type        = string
}

variable "storage_account_id" {
  description = "Storage account resource id for attachment blobs."
  type        = string
  default     = null
}

variable "sql_connection_uri" {
  description = "Credential-free SQL connection string. Authentication is by Managed Identity, so this must contain no password."
  type        = string

  validation {
    condition     = !can(regex("(?i)(password|pwd)\\s*=", var.sql_connection_uri))
    error_message = "The SQL connection string must not contain a password. Use Managed Identity authentication."
  }
}

variable "app_insights_connection_string" {
  description = "Application Insights connection string."
  type        = string
  sensitive   = true
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic settings."
  type        = string
}

variable "entra_client_id" {
  description = "Entra ID application (client) id for the API."
  type        = string
}

variable "tenant_id" {
  description = "Entra ID tenant id."
  type        = string
}

variable "allowed_cors_origins" {
  description = "Origins permitted to call the API."
  type        = list(string)
  default     = []
}

variable "additional_app_settings" {
  description = "Environment-specific app settings merged over the common set."
  type        = map(string)
  default     = {}
}

variable "enable_staging_slot" {
  description = "Create a staging slot for swap-based deployment. Required in prd."
  type        = bool
  default     = true
}

variable "tags" {
  description = "Resource tags."
  type        = map(string)
  default     = {}
}

variable "storage_blob_endpoint" {
  description = "Blob service endpoint for the attachments account, e.g. https://stx.blob.core.windows.net. Empty disables attachment upload."
  type        = string
  default     = ""
}

variable "attachments_container_name" {
  description = "Container holding receipt attachments. Provisioned with a WORM retention policy by modules/storage."
  type        = string
  default     = "attachments"
}
