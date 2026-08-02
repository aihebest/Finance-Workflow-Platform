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
  default     = "dev"

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
  description = "Display name for the SQL Entra ID administrator (typically a security group, e.g. 'sql-admins-dev')."
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
  description = "Repository and tag only, with NO registry host -- e.g. \"finance-workflow-api:latest\", not \"crdesiconfwdev.azurecr.io/finance-workflow-api:latest\". App Service concatenates docker_registry_url with docker_image_name, so a host in both produces a doubled reference whose pull fails with a 401/403 token error that reads as a credentials problem. Bootstrap value only: modules/app-service ignores subsequent changes to docker_image_name so the CI deploy job owns the tag."
  type        = string

  validation {
    condition     = !can(regex("\\.(azurecr\\.io|io|com)/", var.container_image))
    error_message = "container_image must not include a registry host -- give repository:tag only (e.g. \"finance-workflow-api:latest\"). The host comes from the registry module."
  }
}

# container_registry_url and container_registry_id were root variables when
# the registry lived outside Terraform (GHCR). The registry is now
# modules/acr, so app_service takes module.acr.registry_url / module.acr.id
# directly and these variables no longer exist.

variable "deployer_ip_addresses" {
  description = "Public IPs/CIDRs of Terraform deploy agents (developer laptops, CI runners) to allow through the network_acls/network_rules/firewall rules of Key Vault, SQL, the attachments storage account and the Functions runtime storage account. Dev drops private endpoints entirely (use_private_endpoints = false throughout, see docs/02-Solution-Architecture.md), so each of these must open a narrow, IP-pinned exception or terraform apply cannot reach the data plane it needs (the SQL TDE key / Storage CMK, the database itself, blob/queue/table for the runtime storage). Required precisely because it's dev -- see policy/terraform/azure_security.rego, which denies public access unconditionally outside dev. Give single addresses without a /32 suffix (e.g. \"203.0.113.5\", not \"203.0.113.5/32\") -- Storage's network_rules.ip_rules rejects /31 and /32 CIDR suffixes outright (Azure only accepts /0-/30 there), while Key Vault and SQL's firewall rules accept either form; a bare IP satisfies all three."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for v in var.deployer_ip_addresses : can(regex("^([0-9]{1,3}\\.){3}[0-9]{1,3}$", v))])
    error_message = "Each address must be a bare IPv4 address with no CIDR suffix (e.g. \"203.0.113.5\", not \"203.0.113.5/32\"). Azure SQL firewall rules take a bare IP, not a CIDR, and Storage's network_rules.ip_rules rejects /31 and /32 outright -- only prefixes /0-/30 are valid there. A bare IP is the only form all three services (Key Vault, SQL, Storage) accept."
  }
}

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

# ── Sizing (scaled down relative to uat/prd) ───────────────────────────────
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
