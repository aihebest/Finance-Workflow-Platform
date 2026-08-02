variable "name" {
  description = "Storage account name, e.g. stdesiconfwdev. Globally unique, lowercase alphanumeric, 3-24 chars."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "replication_type" {
  description = "Storage replication type."
  type        = string
  default     = "GRS"
}

variable "key_vault_id" {
  description = "Key Vault resource id, for the storage account's customer-managed key."
  type        = string
}

variable "immutability_period_in_days" {
  description = "WORM retention for the attachments container. Defaults to ~7 years to match the platform's audit retention requirement."
  type        = number
  default     = 2557
}

variable "immutability_locked" {
  description = "Lock the immutability policy. Irreversible -- also prevents the storage account and container from ever being deleted. Leave false in dev/uat; set true in prd only once verified."
  type        = bool
  default     = false
}

variable "pe_subnet_id" {
  description = "Subnet for the private endpoint. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "private_dns_zone_id" {
  description = "privatelink.blob.core.windows.net zone id, from the network module. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "use_private_endpoints" {
  description = "Reach this account only over a private endpoint. Default true: uat and prd. When false (dev only), the private endpoint is skipped entirely and public_network_access_enabled/ip_rules below take over -- see policy/terraform/azure_security.rego, which denies public access unconditionally outside dev."
  type        = bool
  default     = true
}

variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace for diagnostic settings."
  type        = string
}

variable "tags" {
  description = "Resource tags."
  type        = map(string)
  default     = {}
}

# ── Public network access (dev only -- see policy/terraform/azure_security.rego) ──
# Same problem, same shape as Key Vault's exception: the private endpoint is
# the only network path Terraform itself cannot use, so a deploy agent
# outside the VNet has no route to the blob data plane it needs to hit at
# create time. Production must run from inside the VNet (a self-hosted
# runner in the app subnet -- see docs/02-Solution-Architecture.md) and
# stays fully locked down; dev/uat may open a narrow, IP-pinned exception.
variable "public_network_access_enabled" {
  description = "Allow public network access, gated by network_rules (default_action = Deny, ip_rules allow-list). Must be false in production -- policy denies it unconditionally when environment == \"prd\"."
  type        = bool
  default     = false
}

variable "ip_rules" {
  description = "Public IPs/CIDRs allowed through network_rules when public_network_access_enabled is true (e.g. the deploy agent's egress IP). Ignored when public_network_access_enabled is false, since network_rules default_action = Deny + bypass = AzureServices is set unconditionally."
  type        = list(string)
  default     = []
}
