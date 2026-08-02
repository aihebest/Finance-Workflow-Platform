variable "name_prefix" {
  description = "Naming prefix, e.g. desicon-fw-dev. Used to derive VNet, subnet and NSG names."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region. South Africa North is the closest region to Lagos with a paired region."
  type        = string
}

variable "address_space" {
  description = "VNet address space."
  type        = list(string)
  default     = ["10.20.0.0/16"]
}

variable "app_subnet_address_prefixes" {
  description = "Address prefixes for snet-app, the VNet-integration target for App Service and Functions."
  type        = list(string)
  default     = ["10.20.1.0/24"]
}

variable "pe_subnet_address_prefixes" {
  description = "Address prefixes for snet-pe, where every data service's private endpoint lands."
  type        = list(string)
  default     = ["10.20.2.0/24"]
}

variable "tags" {
  description = "Resource tags."
  type        = map(string)
  default     = {}
}

variable "use_private_endpoints" {
  description = "Create the private DNS zones and VNet links every data-plane module's private endpoint depends on. Default true: uat and prd run the full private-endpoint topology. Dev sets this false -- its data services trade the private endpoint for IP-restricted public access instead (see policy/terraform/azure_security.rego), so these zones would have nothing to resolve."
  type        = bool
  default     = true
}
