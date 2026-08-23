output "profile_id" {
  description = "Front Door profile resource id."
  value       = azurerm_cdn_frontdoor_profile.this.id
}

output "frontdoor_id" {
  description = "Front Door resource GUID, sent as the X-Azure-FDID header. Wire this into the app-service module's front_door_id variable so App Service only accepts traffic carrying this id."
  value       = azurerm_cdn_frontdoor_profile.this.resource_guid
}

output "endpoint_hostname" {
  description = "Default *.azurefd.net hostname for the endpoint."
  value       = azurerm_cdn_frontdoor_endpoint.this.host_name
}

output "web_route_id" {
  description = "SPA route id, or null when no web origin is configured."
  value       = var.web_origin_hostname == null ? null : azurerm_cdn_frontdoor_route.web[0].id
}

output "custom_domain_host_name" {
  description = "The custom FQDN this environment serves on, or null when only the azurefd.net hostname is configured."
  value       = var.custom_domain_host_name
}

output "custom_domain_dns_records" {
  description = <<-EOT
    The two DNS records to publish in the Microsoft 365 admin center
    (Settings -> Domains -> desiconapp.com -> DNS records -> Add record).

    Publish the TXT first and wait for Front Door to report the domain as
    Approved -- typically about fifteen minutes. Publish the CNAME only after
    that. A CNAME pointed at Front Door before the domain is validated and
    routed resolves and then serves an error, which is indistinguishable from
    a broken deployment to anyone who tries the address in the meantime.
  EOT
  value = var.custom_domain_host_name == null ? null : {
    validation_txt = {
      type  = "TXT"
      name  = "_dnsauth.${split(".", var.custom_domain_host_name)[0]}"
      value = azurerm_cdn_frontdoor_custom_domain.this[0].validation_token
      ttl   = 3600
      note  = "Proves ownership to Front Door. Not the same as the asuid.* records already on this domain, which belong to App Service."
    }
    alias_cname = {
      type  = "CNAME"
      name  = split(".", var.custom_domain_host_name)[0]
      value = azurerm_cdn_frontdoor_endpoint.this.host_name
      ttl   = 3600
      note  = "Publish only after the TXT has validated and the domain shows Approved."
    }
  }

  # validation_token is a domain-ownership challenge, not a credential, and it
  # is useless without control of the DNS zone it must be published in. Marking
  # it sensitive would hide it from `terraform output` and force whoever is
  # copying it into the admin center to dig it out of state instead.
  sensitive = false
}

output "custom_domain_expiration_date" {
  description = "When the domain validation token expires. Ownership must be revalidated before this date if the domain has not been approved by then."
  value       = var.custom_domain_host_name == null ? null : azurerm_cdn_frontdoor_custom_domain.this[0].expiration_date
}
