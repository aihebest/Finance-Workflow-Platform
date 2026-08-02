variable "name" {
  description = "Key Vault name, e.g. kv-desicon-fw-dev. Globally unique, 3-24 chars."
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

variable "tenant_id" {
  description = "Entra ID tenant id."
  type        = string
}

variable "soft_delete_retention_days" {
  description = "Soft-delete retention in days. Must be at least 90 -- see policy/terraform/azure_security.rego."
  type        = number
  default     = 90

  validation {
    condition     = var.soft_delete_retention_days >= 90
    error_message = "soft_delete_retention_days must be at least 90."
  }
}

variable "administrator_object_ids" {
  description = "Entra object ids granted 'Key Vault Administrator' (e.g. the break-glass admin group, or the identity running Terraform so it can create the SQL TDE key and Storage CMK). RBAC grants nothing by default -- unlike access policies, creating the vault does not implicitly grant its creator access."
  type        = list(string)
  default     = []
}

variable "pe_subnet_id" {
  description = "Subnet for the private endpoint. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "private_dns_zone_id" {
  description = "privatelink.vaultcore.azure.net zone id, from the network module. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "use_private_endpoints" {
  description = "Reach this vault only over a private endpoint. Default true: uat and prd. When false (dev only), the private endpoint is skipped entirely and public_network_access_enabled/ip_rules below take over -- see policy/terraform/azure_security.rego, which denies public access unconditionally outside dev."
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
# The private endpoint above is the only network path Terraform itself
# cannot use: a deploy agent running outside the VNet (a developer laptop, a
# GitHub-hosted runner) has no route to it, so `terraform apply` cannot
# create keys/secrets in a vault that's fully network-isolated from its own
# creator. Production must run from inside the VNet (a self-hosted runner in
# the app subnet -- see docs/02-Solution-Architecture.md) and stays fully
# locked down; dev/uat may open a narrow, IP-pinned exception instead.
variable "public_network_access_enabled" {
  description = "Allow public network access, gated by network_acls (default_action = Deny, ip_rules allow-list). Must be false in production -- policy denies it unconditionally when environment == \"prd\"."
  type        = bool
  default     = false
}

variable "ip_rules" {
  description = "Public IPs/CIDRs allowed through network_acls when public_network_access_enabled is true (e.g. the deploy agent's egress IP). Ignored when public_network_access_enabled is false, since network_acls default_action = Deny + bypass = AzureServices is set unconditionally."
  type        = list(string)
  default     = []
}
