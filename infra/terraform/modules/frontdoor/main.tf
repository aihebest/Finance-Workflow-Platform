###############################################################################
# Desicon Finance Workflow Platform — Front Door module
#
# Azure Front Door (Premium, for managed WAF rule sets) + WAF policy in
# Prevention mode. This is the only public entry point to the platform --
# App Service accepts traffic solely from Front Door's backend service tag
# (see the app-service module's ip_restriction, keyed off this module's
# `frontdoor_id` output) so every request is WAF-inspected before it reaches
# application code.
#
# Usage:
#   module "frontdoor" {
#     source              = "../../modules/frontdoor"
#     name                = "desicon-fw-dev"
#     resource_group_name = azurerm_resource_group.main.name
#     origin_hostname     = module.app_service.default_hostname
#     health_probe_path   = "/health/ready"
#     log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
#     tags                = local.tags
#   }
###############################################################################

terraform {
  required_version = ">= 1.7.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

locals {
  # WAF policy names must be alphanumeric only -- no hyphens.
  waf_policy_name = replace("waf${var.name}", "-", "")
}

resource "azurerm_cdn_frontdoor_profile" "this" {
  name                = "afd-${var.name}"
  resource_group_name = var.resource_group_name
  sku_name            = var.sku_name # Premium: managed WAF rule sets + Bot Manager.

  response_timeout_seconds = 120

  tags = var.tags
}

resource "azurerm_cdn_frontdoor_endpoint" "this" {
  name                     = "fde-${var.name}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.this.id

  tags = var.tags
}

resource "azurerm_cdn_frontdoor_origin_group" "this" {
  name                     = "og-${var.name}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.this.id
  session_affinity_enabled = true

  health_probe {
    protocol            = "Https"
    interval_in_seconds = 30
    request_type        = "GET"
    path                = var.health_probe_path
  }

  load_balancing {
    additional_latency_in_milliseconds = 0
    sample_size                        = 4
    successful_samples_required        = 3
  }
}

resource "azurerm_cdn_frontdoor_origin" "this" {
  name                          = "origin-${var.name}"
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.this.id
  enabled                       = true

  certificate_name_check_enabled = true # Verify the origin's TLS cert name -- this is a public origin, not a private-link one.

  host_name          = var.origin_hostname
  http_port          = 80
  https_port         = 443
  origin_host_header = var.origin_hostname
  priority           = 1
  weight             = 1000
}

# ── SPA origin (optional) ─────────────────────────────────────────────────
# Present only when web_origin_hostname is set. Serving the SPA and the API
# from one origin matters beyond tidiness: same-origin means the browser sends
# no preflight and the API needs no CORS entry, so there is no allow-list to
# get wrong and no third-party origin to trust. It also means the SPA's
# Content-Security-Policy can keep connect-src at 'self'.
resource "azurerm_cdn_frontdoor_origin_group" "web" {
  count                    = var.web_origin_hostname == null ? 0 : 1
  name                     = "og-web-${var.name}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.this.id
  session_affinity_enabled = false # Static files; any instance serves any request.

  health_probe {
    protocol            = "Https"
    interval_in_seconds = 30
    request_type        = "GET"
    path                = var.web_health_probe_path
  }

  load_balancing {
    additional_latency_in_milliseconds = 0
    sample_size                        = 4
    successful_samples_required        = 3
  }
}

resource "azurerm_cdn_frontdoor_origin" "web" {
  count                         = var.web_origin_hostname == null ? 0 : 1
  name                          = "origin-web-${var.name}"
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.web[0].id
  enabled                       = true

  certificate_name_check_enabled = true

  host_name          = var.web_origin_hostname
  http_port          = 80
  https_port         = 443
  origin_host_header = var.web_origin_hostname
  priority           = 1
  weight             = 1000
}

# ── Routes ────────────────────────────────────────────────────────────────
# Front Door matches the most specific pattern, so /api/* wins over /* and
# ordering here does not matter. When no SPA origin exists the API keeps /*,
# which is the shape this module had before the frontend existed.
resource "azurerm_cdn_frontdoor_route" "this" {
  name                          = "route-${var.name}"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.this.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.this.id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.this.id]

  patterns_to_match      = var.web_origin_hostname == null ? ["/*"] : ["/api/*", "/health/*"]
  supported_protocols    = ["Http", "Https"]
  forwarding_protocol    = "HttpsOnly"
  https_redirect_enabled = true
  link_to_default_domain = true

  # No cache block: API responses are per-user and must not be cached at the edge.
}

resource "azurerm_cdn_frontdoor_route" "web" {
  count                         = var.web_origin_hostname == null ? 0 : 1
  name                          = "route-web-${var.name}"
  cdn_frontdoor_endpoint_id     = azurerm_cdn_frontdoor_endpoint.this.id
  cdn_frontdoor_origin_group_id = azurerm_cdn_frontdoor_origin_group.web[0].id
  cdn_frontdoor_origin_ids      = [azurerm_cdn_frontdoor_origin.web[0].id]

  patterns_to_match      = ["/*"]
  supported_protocols    = ["Http", "Https"]
  forwarding_protocol    = "HttpsOnly"
  https_redirect_enabled = true
  link_to_default_domain = true

  # No cache block here either, deliberately. Vite emits content-hashed
  # filenames and nginx already sets immutable long-lived caching on them,
  # while index.html must never be cached or a deploy leaves browsers holding
  # a page that references bundles which no longer exist. Edge caching would
  # need per-path rules to express that; the origin already expresses it
  # correctly, so this defers to the origin.
}

resource "azurerm_cdn_frontdoor_firewall_policy" "this" {
  name                = local.waf_policy_name
  resource_group_name = var.resource_group_name
  sku_name            = azurerm_cdn_frontdoor_profile.this.sku_name
  enabled             = true
  mode                = var.waf_mode

  managed_rule {
    type    = "Microsoft_DefaultRuleSet"
    version = "2.1"
    action  = "Block"

    # ── Receipt uploads ───────────────────────────────────────────────────
    # Found 9 Aug 2026: every POST to /api/v1/requests/{id}/attachments was
    # blocked here and never reached the API. The panel reported only
    # "Request failed (403)" because Front Door answers with an HTML error
    # page rather than ProblemDetails, so nothing in the application or its
    # logs said the WAF had refused it. 61 integration tests were green --
    # none of them go through Front Door, and each seeds attachment rows
    # straight into the table.
    #
    #   200002  Failed to parse request body
    #   200003  Multipart request body failed strict validation
    #
    # Both scored against 949110 (anomaly threshold) until it blocked. Neither
    # detects an attack: they are protocol-hygiene rules written for form
    # posts, and a JPEG is not a form field. Every rule that looks for an
    # actual attack -- SQLi 942xxx, XSS 941xxx, RCE 932xxx, LFI 930xxx --
    # stays enabled, on this path as on every other.
    #
    # WHY DISABLED GLOBALLY RATHER THAN SCOPED TO THE UPLOAD PATH
    # -----------------------------------------------------------
    # Front Door has no path-scoped exclusion for a managed rule; the only
    # per-path mechanism is a custom rule with action Allow, which skips the
    # entire managed ruleset for whatever it matches. Its match variable is
    # RequestUri, which includes the query string, so a rule matching
    # "ends with /attachments" is satisfied by
    #   POST /api/v1/requests/{id}/actions?x=/attachments
    # -- a real endpoint, with the WAF turned off for it. Trading two
    # parser-hygiene rules site-wide for an attacker-controlled bypass of the
    # whole ruleset is the wrong way round.
    #
    # Worth knowing either way: Front Door inspects only the first 128 KB of
    # a body, so a 10 MB receipt was never being scanned in full regardless.
    # What actually guards this endpoint is in the API -- authentication, the
    # ReadAccessScope check, a content-type allowlist, the 10 MB cap, blob
    # paths built from ids and never from filenames, and a forced
    # Content-Disposition: attachment on the way back out.
    override {
      rule_group_name = "General"

      rule {
        rule_id = "200002"
        enabled = false
        action  = "Log"
      }

      rule {
        rule_id = "200003"
        enabled = false
        action  = "Log"
      }
    }
  }

  managed_rule {
    type    = "Microsoft_BotManagerRuleSet"
    version = "1.0"
    action  = "Log"
  }

  tags = var.tags
}

resource "azurerm_cdn_frontdoor_security_policy" "this" {
  name                     = "secpol-${var.name}"
  cdn_frontdoor_profile_id = azurerm_cdn_frontdoor_profile.this.id

  security_policies {
    firewall {
      cdn_frontdoor_firewall_policy_id = azurerm_cdn_frontdoor_firewall_policy.this.id

      association {
        domain {
          cdn_frontdoor_domain_id = azurerm_cdn_frontdoor_endpoint.this.id
        }
        patterns_to_match = ["/*"]
      }
    }
  }
}

# ── Diagnostics ────────────────────────────────────────────────────────────
resource "azurerm_monitor_diagnostic_setting" "this" {
  name                       = "diag-afd-${var.name}"
  target_resource_id         = azurerm_cdn_frontdoor_profile.this.id
  log_analytics_workspace_id = var.log_analytics_workspace_id

  enabled_log { category = "FrontDoorAccessLog" }
  enabled_log { category = "FrontDoorHealthProbeLog" }
  enabled_log { category = "FrontDoorWebApplicationFirewallLog" }
}
