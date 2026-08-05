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
