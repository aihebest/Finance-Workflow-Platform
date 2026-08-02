variable "name" {
  description = "Registry name. Alphanumeric only, 5-50 chars, globally unique -- no hyphens, unlike every other resource here."
  type        = string

  validation {
    condition     = can(regex("^[a-zA-Z0-9]{5,50}$", var.name))
    error_message = "ACR names are alphanumeric only (no hyphens or underscores) and 5-50 characters."
  }
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "sku" {
  description = "Basic, Standard or Premium. Private endpoints, network rules, geo-replication, content trust and retention policies are Premium-only. Dev runs Basic deliberately: the registry holds no data that is sensitive independent of the image itself, and Premium is roughly ten times the cost for controls dev does not exercise."
  type        = string
  default     = "Basic"

  validation {
    condition     = contains(["Basic", "Standard", "Premium"], var.sku)
    error_message = "sku must be Basic, Standard or Premium."
  }
}

variable "public_network_access_enabled" {
  description = "Allow public network access. Must be true on Basic and Standard, which have no private endpoint support -- setting it false there produces a registry nothing can reach. Production should run Premium with this false and a private endpoint in the pe subnet."
  type        = bool
  default     = true

  validation {
    condition     = var.public_network_access_enabled || var.sku == "Premium"
    error_message = "public_network_access_enabled can only be false on the Premium sku, which is the only tier supporting private endpoints."
  }
}

variable "untagged_retention_days" {
  description = "Days to keep untagged manifests before purge. Premium only; ignored otherwise. Every CI build pushes a SHA-tagged image, so untagged layers accumulate quickly once :latest moves."
  type        = number
  default     = 30
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
