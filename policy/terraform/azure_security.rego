package terraform.azure.security

# Policy-as-code gate over the Terraform *plan*, not the source.
#
# Checkov and tfsec catch generic misconfiguration. These rules encode the
# decisions specific to this platform -- the ones no off-the-shelf ruleset
# knows about, and the ones a reviewer would otherwise have to remember.
#
# Evaluated against `terraform show -json tfplan`, so a value that only
# resolves at plan time (a variable, a module output, a computed reference)
# is still checked. Scanning the .tf source alone misses those.
#
# Run: conftest test --policy policy/terraform plan.json

import rego.v1

# ── Helpers ─────────────────────────────────────────────────────────────────

resources contains resource if {
	some resource in input.resource_changes
	resource.change.actions[_] != "delete"
}

resources_of(kind) := {r |
	some r in resources
	r.type == kind
}

after(r) := r.change.after

is_production if {
	input.variables.environment.value == "prd"
}

# ── Network isolation ───────────────────────────────────────────────────────
# The single highest-value control in the design: no data-plane service is
# reachable from the public internet.

deny contains msg if {
	some r in resources_of("azurerm_mssql_server")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: Azure SQL must not accept public network access. Reach it over the private endpoint from the app subnet.",
		[r.address],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).public_network_access_enabled == true
	msg := sprintf("%s: Key Vault must not accept public network access.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: the attachments storage account must not accept public network access. Receipts are personal and financial data.",
		[r.address],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).allow_nested_items_to_be_public == true
	msg := sprintf("%s: public blob access must be disabled.", [r.address])
}

# ── Transport security ──────────────────────────────────────────────────────

deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).min_tls_version != "TLS1_2"
	msg := sprintf(
		"%s: minimum TLS must be 1.2 (found %v).",
		[r.address, after(r).min_tls_version],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_linux_web_app")
	after(r).https_only != true
	msg := sprintf("%s: https_only must be true.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_linux_web_app")
	some config in after(r).site_config
	config.minimum_tls_version != "1.2"
	msg := sprintf("%s: App Service minimum TLS must be 1.2.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_linux_web_app")
	some config in after(r).site_config
	config.ftps_state != "Disabled"
	msg := sprintf("%s: FTPS must be disabled.", [r.address])
}

# ── Identity: no credentials, anywhere ──────────────────────────────────────
# The platform authenticates to every Azure resource by Managed Identity.
# A resource that supports local/admin auth and leaves it enabled is a
# credential waiting to be leaked.

deny contains msg if {
	some r in resources_of("azurerm_linux_web_app")
	count(after(r).identity) == 0
	msg := sprintf(
		"%s: App Service must have a managed identity. There is no path in this platform that uses a stored credential.",
		[r.address],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_container_registry")
	after(r).admin_enabled == true
	msg := sprintf(
		"%s: ACR admin user must be disabled. Image pull uses the app's managed identity via AcrPull.",
		[r.address],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_mssql_server")
	count(after(r).azuread_administrator) == 0
	msg := sprintf("%s: Azure SQL must have an Entra ID administrator configured.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).enable_rbac_authorization != true
	msg := sprintf(
		"%s: Key Vault must use RBAC authorization rather than access policies, so grants are visible in the same place as every other Azure permission.",
		[r.address],
	)
}

# ── Data protection ─────────────────────────────────────────────────────────

deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).purge_protection_enabled != true
	msg := sprintf(
		"%s: purge protection must be enabled. Without it a deleted vault takes the SQL TDE key with it and the database is unrecoverable.",
		[r.address],
	)
}

deny contains msg if {
	some r in resources_of("azurerm_key_vault")
	after(r).soft_delete_retention_days < 90
	msg := sprintf("%s: soft-delete retention must be at least 90 days.", [r.address])
}

deny contains msg if {
	some r in resources_of("azurerm_storage_account")
	after(r).blob_properties[_].versioning_enabled != true
	msg := sprintf(
		"%s: blob versioning must be enabled. An approved receipt must not be silently replaceable.",
		[r.address],
	)
}

# ── Observability ───────────────────────────────────────────────────────────
# An audit trail nobody can query is not an audit trail.

required_diagnostics := {
	"azurerm_mssql_database",
	"azurerm_key_vault",
	"azurerm_linux_web_app",
	"azurerm_storage_account",
}

diagnostic_targets contains target if {
	some r in resources_of("azurerm_monitor_diagnostic_setting")
	target := after(r).target_resource_id
}

warn contains msg if {
	some kind in required_diagnostics
	some r in resources_of(kind)
	not covered_by_diagnostics(r)
	msg := sprintf(
		"%s: no diagnostic setting found sending logs to Log Analytics.",
		[r.address],
	)
}

covered_by_diagnostics(r) if {
	some target in diagnostic_targets
	contains(target, r.name)
}

# ── Production-only requirements ────────────────────────────────────────────

deny contains msg if {
	is_production
	some r in resources_of("azurerm_mssql_database")
	after(r).zone_redundant != true
	msg := sprintf("%s: production databases must be zone redundant.", [r.address])
}

deny contains msg if {
	is_production
	some r in resources_of("azurerm_mssql_database")
	after(r).sku_name == "Basic"
	msg := sprintf("%s: Basic tier is not permitted in production.", [r.address])
}

# ── Tagging: required for cost attribution and incident ownership ───────────

required_tags := {"environment", "owner", "cost_centre", "data_classification"}

taggable := {
	"azurerm_linux_web_app",
	"azurerm_mssql_server",
	"azurerm_key_vault",
	"azurerm_storage_account",
	"azurerm_service_plan",
}

deny contains msg if {
	some kind in taggable
	some r in resources_of(kind)
	some tag in required_tags
	not after(r).tags[tag]
	msg := sprintf("%s: missing required tag '%s'.", [r.address, tag])
}

# ── Anti-pattern: hardcoded secrets reaching a resource ─────────────────────

deny contains msg if {
	some r in resources_of("azurerm_linux_web_app")
	some key, value in after(r).app_settings
	regex.match(`(?i)(password|pwd|secret|apikey|api_key)\s*=`, value)
	msg := sprintf(
		"%s: app setting '%s' looks like it carries an inline credential. Reference Key Vault instead.",
		[r.address, key],
	)
}
