variable "name" {
  description = "Web App name, e.g. app-desicon-fw-web-dev. Globally unique."
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
  description = "App Service Plan SKU. A SPA behind Front Door serves static files from nginx and needs very little; the plan is sized for availability rather than throughput."
  type        = string
  default     = "B1"
}

variable "always_on" {
  description = "Keep the container warm. False on the free/shared tiers, which do not support it. A cold start on a static site is a blank page for several seconds, so this should be true anywhere a person will use it."
  type        = bool
  default     = true
}

variable "container_image" {
  description = "Repository and tag only, with NO registry host -- App Service composes the pull reference from docker_registry_url + docker_image_name, and a host in both yields a doubled reference whose failure reads as a credentials error. Bootstrap value only: ignore_changes hands the tag to the deploy job."
  type        = string

  validation {
    condition     = !can(regex("\\.(azurecr\\.io|io|com)/", var.container_image))
    error_message = "container_image must not include a registry host -- give repository:tag only."
  }
}

variable "container_registry_url" {
  description = "Registry login server with scheme, e.g. https://crdesiconfwdev.azurecr.io."
  type        = string
}

variable "container_registry_id" {
  description = "Registry resource id, for the AcrPull role assignment."
  type        = string
}

variable "front_door_id" {
  description = "Front Door profile resource GUID. When set, the app accepts traffic only from Front Door carrying this id, so the WAF cannot be bypassed via the default azurewebsites.net hostname. Null disables the restriction, which should only happen where no Front Door exists."
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
