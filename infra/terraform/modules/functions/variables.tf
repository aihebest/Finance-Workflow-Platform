variable "name" {
  description = "Function App name, e.g. func-desicon-fw-dev."
  type        = string
}

variable "storage_account_name" {
  description = "Runtime storage account name, e.g. stdesiconfwdevfn. Globally unique, lowercase alphanumeric, 3-24 chars."
  type        = string
}

variable "storage_name_suffix" {
  description = "Appended to storage_account_name verbatim. Empty by default. An escape hatch for retiring a storage account whose name is stuck -- e.g. one where the blob data plane never comes up (ARM reports Succeeded but the account 404s indefinitely) -- without freeing up and reusing the original, possibly still-wedged, name."
  type        = string
  default     = ""
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region."
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
  description = "Function App Service Plan SKU. EP1 (Elastic Premium) for VNet integration and no cold start on the timer sweeps."
  type        = string
  default     = "EP1"
}

variable "storage_replication_type" {
  description = "Replication type for the runtime storage account."
  type        = string
  default     = "LRS"
}

variable "app_subnet_id" {
  description = "Subnet for VNet integration."
  type        = string
}

variable "pe_subnet_id" {
  description = "Subnet for the runtime storage account's private endpoints. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "private_dns_zone_ids" {
  description = "Map of private DNS zone ids from the network module. Must contain the 'blob', 'queue' and 'table' keys when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = map(string)
  default     = {}
}

variable "use_private_endpoints" {
  description = "Reach the runtime storage account only over private endpoints. Default true: uat and prd. When false (dev only), the private endpoints are skipped and storage_public_network_access_enabled/storage_ip_rules below take over instead -- see policy/terraform/azure_security.rego, which denies public access unconditionally outside dev."
  type        = bool
  default     = true
}

variable "public_network_access_enabled" {
  description = "Whether the Function App accepts traffic outside the VNet. Nothing about this app is user-facing, so this defaults to false unlike App Service."
  type        = bool
  default     = false
}

variable "key_vault_id" {
  description = "Key Vault resource id, for the Secrets User role assignment."
  type        = string
}

variable "key_vault_uri" {
  description = "Key Vault URI passed to the app."
  type        = string
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

variable "additional_app_settings" {
  description = "Environment-specific app settings merged over the common set."
  type        = map(string)
  default     = {}
}

variable "tags" {
  description = "Resource tags."
  type        = map(string)
  default     = {}
}

# ── Runtime storage public network access (dev only) ────────────────────────
# Distinct from public_network_access_enabled above, which gates the
# Function App itself: this gates the runtime storage account's own data
# plane. Same problem as Key Vault and the attachments storage account -- the
# private endpoint is the only network path Terraform itself cannot use, so
# a deploy agent outside the VNet has no route to it at create time. See
# policy/terraform/azure_security.rego and docs/02-Solution-Architecture.md.
variable "storage_public_network_access_enabled" {
  description = "Allow public network access to the runtime storage account, gated by network_rules (default_action = Deny, ip_rules allow-list). Must be false in production -- policy denies it unconditionally when environment == \"prd\"."
  type        = bool
  default     = false
}

variable "storage_ip_rules" {
  description = "Public IPs/CIDRs allowed through the runtime storage account's network_rules when storage_public_network_access_enabled is true. Ignored when storage_public_network_access_enabled is false, since network_rules default_action = Deny + bypass = AzureServices is set unconditionally."
  type        = list(string)
  default     = []
}

variable "vnet_rule_subnet_ids" {
  description = "Subnets admitted through the runtime storage account's network_rules. Required in dev, where use_private_endpoints is false: the Function App reaches AzureWebJobsStorage and its run-from-package blob from the integrated app subnet, and storage_ip_rules only covers public deployer addresses. Each subnet must advertise the Microsoft.Storage service endpoint -- a rule naming a subnet without it is accepted and silently never matches. Symptom when missing: the host will not start, and syncfunctiontriggers reports an InternalServerError from the host runtime."
  type        = list(string)
  default     = []
}

variable "notifications_use_graph" {
  description = "Send notifications via Microsoft Graph. False makes the dispatcher log what it would have sent instead, which is correct until Exchange provisions the shared mailbox and grants Mail.Send. Stated explicitly per environment rather than inferred from whether a mailbox is configured: a deployment that lost its configuration must fail visibly, not quietly stop sending mail while reporting success."
  type        = bool
  default     = false
}

variable "notifications_sender_mailbox" {
  description = "Shared mailbox the platform sends as, e.g. finance-workflow@desicongroup.com. Must be constrained by an Exchange application access policy limited to this one mailbox -- Mail.Send as an application permission otherwise lets the platform email as anyone in the tenant. That policy is Exchange configuration and cannot be enforced from here."
  type        = string
  default     = ""
}

variable "notifications_application_base_url" {
  description = "Base URL used to build the deep link in every notification, e.g. https://fde-desicon-fw-dev-xxxx.z01.azurefd.net. No trailing slash. Empty renders notifications without a link rather than with a broken one."
  type        = string
  default     = ""
}
