variable "name" {
  description = "Naming suffix, e.g. desicon-fw-dev."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into. Front Door is a global service, so this module takes no `location`."
  type        = string
}

variable "sku_name" {
  description = "Front Door SKU. Premium is required for managed WAF rule sets and Bot Manager."
  type        = string
  default     = "Premium_AzureFrontDoor"
}

variable "origin_hostname" {
  description = "Origin hostname, e.g. the App Service default hostname."
  type        = string
}

variable "health_probe_path" {
  description = "Path Front Door probes to determine origin health."
  type        = string
  default     = "/health/ready"
}

variable "waf_mode" {
  description = "WAF policy mode. Prevention blocks matching traffic; Detection only logs it."
  type        = string
  default     = "Prevention"

  validation {
    condition     = contains(["Prevention", "Detection"], var.waf_mode)
    error_message = "waf_mode must be Prevention or Detection."
  }
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

variable "web_origin_hostname" {
  description = "Hostname of the SPA origin. When set, the API route narrows to /api/* and /health/*, and everything else routes to the SPA -- so both are served from one origin and the browser never makes a cross-origin call. Null keeps the API on /*, which is the shape before a frontend existed."
  type        = string
  default     = null
}

variable "web_health_probe_path" {
  description = "Health probe path on the SPA origin. docker/nginx.conf serves /healthz; probing / instead would work but returns the full index.html on every probe, thirty seconds apart, forever."
  type        = string
  default     = "/healthz"
}
