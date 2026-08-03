variable "name" {
  description = "SQL logical server name, e.g. sql-desicon-fw-dev. Globally unique."
  type        = string
}

variable "database_name" {
  description = "Database name, e.g. DesiconFinanceWorkflow."
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

variable "sku_name" {
  description = "Database SKU. Business Critical (BC_Gen5_N) gives the in-region SLA and read-replica capacity the architecture relies on."
  type        = string
  default     = "BC_Gen5_2"
}

variable "zone_redundant" {
  description = "Spread replicas across availability zones. Required in prd."
  type        = bool
  default     = false
}

variable "key_vault_id" {
  description = "Key Vault resource id, for the TDE customer-managed key."
  type        = string
}

variable "entra_admin_login" {
  description = "Display name for the Entra ID administrator (typically a security group, e.g. 'sql-admins-dev')."
  type        = string
}

variable "entra_admin_object_id" {
  description = "Object id of the Entra ID administrator (user or, preferably, group)."
  type        = string
}

variable "vnet_rule_subnet_ids" {
  description = "Subnets whose outbound traffic may reach this server, as azurerm_mssql_virtual_network_rule entries. Required in dev, where use_private_endpoints is false: ip_rules admits deployer public addresses, but the App Service and Function App reach SQL from the integrated app subnet with a private source address that no firewall rule can match. Each subnet must advertise the Microsoft.Sql service endpoint -- a rule naming a subnet without it is accepted and never matches. Ignored when use_private_endpoints is true, since the private endpoint is then the only path."
  type        = list(string)
  default     = []
}

variable "pe_subnet_id" {
  description = "Subnet for the private endpoint. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
}

variable "private_dns_zone_id" {
  description = "privatelink.database.windows.net zone id, from the network module. Required when use_private_endpoints is true; unused (and safe to omit) otherwise."
  type        = string
  default     = null
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
# cannot use: a deploy agent running outside the VNet has no route to it, so
# `terraform apply` cannot reach the server to create the database or
# firewall rules. uat and prd run from a self-hosted runner inside the app
# subnet (see docs/02-Solution-Architecture.md) and stay fully locked down;
# dev may open a narrow, IP-pinned exception instead.
variable "use_private_endpoints" {
  description = "Reach this server only over a private endpoint. Default true: uat and prd. When false (dev only), the private endpoint is skipped and public_network_access_enabled/ip_rules below take over instead."
  type        = bool
  default     = true
}

variable "public_network_access_enabled" {
  description = "Allow public network access, gated by a per-address firewall rule built from ip_rules. Must be false whenever use_private_endpoints is true -- policy denies it unconditionally outside dev regardless."
  type        = bool
  default     = false
}

variable "ip_rules" {
  description = "Public IPs allowed through the server firewall when public_network_access_enabled is true (e.g. the deploy agent's egress IP). One azurerm_mssql_firewall_rule is created per address. Ignored when use_private_endpoints is true."
  type        = list(string)
  default     = []
}
