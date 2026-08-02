variable "subscription_id" {
  description = "Azure subscription id to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region. South Africa North is the closest region to Lagos with a paired region."
  type        = string
  default     = "South Africa North"
}

variable "environment" {
  description = "Environment name. Fixed per environments/<env> root."
  type        = string
  default     = "uat"

  validation {
    condition     = contains(["dev", "uat", "prd"], var.environment)
    error_message = "Environment must be one of: dev, uat, prd."
  }
}

variable "owner" {
  description = "Tag: team or individual accountable for these resources."
  type        = string
}

variable "cost_centre" {
  description = "Tag: cost centre for chargeback."
  type        = string
}

# ── Identity ─────────────────────────────────────────────────────────────
variable "entra_admin_login" {
  description = "Display name for the SQL Entra ID administrator (typically a security group, e.g. 'sql-admins-uat')."
  type        = string
}

variable "entra_admin_object_id" {
  description = "Object id of the SQL Entra ID administrator (user or, preferably, group)."
  type        = string
}

variable "entra_client_id" {
  description = "Entra ID application (client) id for the API, used by app-service's auth_settings_v2."
  type        = string
}

variable "keyvault_administrator_object_ids" {
  description = "Additional Entra object ids granted 'Key Vault Administrator', beyond the identity running Terraform (which is always included so it can create the SQL TDE key and Storage CMK)."
  type        = list(string)
  default     = []
}

# ── Application ─────────────────────────────────────────────────────────
variable "container_image" {
  description = "Fully qualified container image for the API, including tag."
  type        = string
}

variable "container_registry_url" {
  description = "Registry login server URL."
  type        = string
}

variable "container_registry_id" {
  description = "Registry resource id, for the AcrPull role assignment. Null if the registry is outside this Terraform's scope and access is granted separately."
  type        = string
  default     = null
}

# No deployer_ip_addresses here, deliberately: uat runs the full
# private-endpoint topology with no public access on any data service, so
# there is no IP-pinned exception to configure. Terraform must run from the
# self-hosted runner inside the app subnet -- see docs/02-Solution-
# Architecture.md "Environments" and policy/terraform/azure_security.rego,
# which denies public network access unconditionally outside dev. If this
# environment ever needs the same escape hatch dev has, mirror
# environments/dev/variables.tf's deployer_ip_addresses variable exactly,
# including its validation block -- bare IPv4 addresses only, no CIDR
# suffix (Azure SQL firewall rules take a bare IP, and Storage's
# network_rules.ip_rules rejects /31 and /32 outright).

variable "allowed_cors_origins" {
  description = "Origins permitted to call the API."
  type        = list(string)
  default     = []
}

variable "alert_email_addresses" {
  description = "Addresses notified by the monitoring action group."
  type        = list(string)
  default     = []
}

# ── Sizing (scaled down relative to prd, network topology identical) ──────
variable "app_service_sku_name" {
  description = "App Service Plan SKU."
  type        = string
  default     = "P1v3"
}

variable "sql_sku_name" {
  description = "SQL database SKU."
  type        = string
  default     = "GP_Gen5_2"
}

variable "functions_sku_name" {
  description = "Function App Service Plan SKU."
  type        = string
  default     = "EP1"
}
