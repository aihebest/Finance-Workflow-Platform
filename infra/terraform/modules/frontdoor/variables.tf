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

variable "custom_domain_host_name" {
  description = <<-EOT
    FQDN to serve this environment on, e.g. finance-dev.desiconapp.com. Null
    keeps only the generated *.azurefd.net hostname.

    Additive: link_to_default_domain stays true on both routes, so the
    azurefd.net address keeps working after this is set. Nothing that already
    points at the old hostname breaks.

    Must be 64 characters or fewer -- Front Door will not issue a managed
    certificate for a longer name.
  EOT
  type        = string
  default     = null

  validation {
    condition     = var.custom_domain_host_name == null || length(coalesce(var.custom_domain_host_name, "")) <= 64
    error_message = "custom_domain_host_name must be 64 characters or fewer; Front Door managed certificates are not issued above that."
  }

  validation {
    condition     = var.custom_domain_host_name == null || can(regex("^[a-z0-9]([a-z0-9-]*[a-z0-9])?\\.[a-z0-9.-]+\\.[a-z]{2,}$", coalesce(var.custom_domain_host_name, "")))
    error_message = "custom_domain_host_name must be a lowercase subdomain FQDN such as finance-dev.desiconapp.com. An apex domain is rejected deliberately: apex managed certificates need revalidation on rotation."
  }
}

variable "custom_domain_dns_zone_id" {
  description = <<-EOT
    Azure DNS zone id for custom_domain_host_name, when the zone is reachable
    by the credentials running this Terraform. Setting it lets Front Door
    validate ownership automatically.

    Left null for Desicon, and it must stay null: desiconapp.com is
    "Managed at Microsoft 365", not hosted in an Azure DNS zone. There is no
    zone resource id to give this, and pointing it at one would be wrong
    rather than merely unhelpful.

    Ownership is proved instead by publishing the _dnsauth TXT record in the
    Microsoft 365 admin center (Settings -> Domains -> desiconapp.com -> DNS
    records -> Add record). The custom_domain_dns_records output prints
    exactly what to create.
  EOT
  type        = string
  default     = null
}
