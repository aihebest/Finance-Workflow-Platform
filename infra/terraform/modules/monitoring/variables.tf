variable "name_prefix" {
  description = "Naming prefix, e.g. desicon-fw-dev."
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

variable "log_retention_in_days" {
  description = "Log Analytics workspace retention."
  type        = number
  default     = 90
}

variable "action_group_short_name" {
  description = "Action group short name (max 12 chars)."
  type        = string
  default     = "desicon-fw"

  validation {
    condition     = length(var.action_group_short_name) <= 12
    error_message = "action_group_short_name must be 12 characters or fewer."
  }
}

variable "alert_email_addresses" {
  description = "Addresses notified by the action group."
  type        = list(string)
  default     = []
}

variable "tags" {
  description = "Resource tags."
  type        = map(string)
  default     = {}
}
