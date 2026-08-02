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

is_dev if {
	input.variables.environment.value == "dev"
}

# ── Network isolation ───────────────────────────────────────────────────────
# The single highest-value control in the design: no data-plane service is
# reachable from the public internet -- outside dev.
#
# A blanket "public_network_access_enabled must be false" is unachievable
# from any deploy agent outside the VNet: Terraform itself has to reach the
# vault/server/account to create the SQL TDE key, Storage CMK and firewall
# entries, and the private endpoint is invisible to anything not already on
# the VNet. A rule nobody can satisfy gets switched off rather than fixed,
# which defeats the point. uat and prd both run from a self-hosted runner
# inside the app subnet (see docs/02-Solution-Architecture.md) and so have
# no such excuse -- both stay denied outright, unconditionally, regardless
# of network_acls/network_rules. Only dev, which has no such runner, may
# trade the private endpoint (use_private_endpoints = false on the module)
# for a narrow, IP-pinned exception -- default_action = Deny with a
# non-empty ip_rules allow-list. Anything looser (default_action = Allow, or
# no ip_rules at all) is exactly the wide-open resource the ban exists to
# prevent, so it's denied there too.

deny contains msg if {
	not is_dev
	some r in resources_of("azurerm_mssql_server")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: Azure SQL must not accept public network access outside dev. Reach it over the private endpoint from the app subnet.",
		[r.address],
	)
}

deny contains msg if {
	is_dev
	some r in resources_of("azurerm_mssql_server")
	after(r).public_network_access_enabled == true
	not sql_firewall_locked_down(r)
	msg := sprintf(
		"%s: Azure SQL allows public network access with no firewall rule allow-listing specific deployer IPs -- an empty ip_rules leaves it open to the internet.",
		[r.address],
	)
}

# SQL has no network_acls-style inline block -- each allowed address is its
# own azurerm_mssql_firewall_rule resource (modules/sql). "Locked down" here
# means at least one such rule exists in the same module instance as the
# server; an empty ip_rules produces none at all.
sql_firewall_locked_down(r) if {
	some fw in resources_of("azurerm_mssql_firewall_rule")
	object.get(fw, "module_address", "") == object.get(r, "module_address", "")
}

deny contains msg if {
	not is_dev
	some r in resources_of("azurerm_key_vault")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: Key Vault must not accept public network access outside dev. Deploy from the self-hosted runner inside the VNet instead.",
		[r.address],
	)
}

deny contains msg if {
	is_dev
	some r in resources_of("azurerm_key_vault")
	after(r).public_network_access_enabled == true
	not key_vault_network_acls_locked_down(r)
	msg := sprintf(
		"%s: Key Vault allows public network access without a locked-down network_acls -- default_action must be 'Deny' and ip_rules must list specific allowed addresses, not be left open or empty.",
		[r.address],
	)
}

key_vault_network_acls_locked_down(r) if {
	some acl in after(r).network_acls
	acl.default_action == "Deny"
	count([ip | some ip in acl.ip_rules]) > 0
}

deny contains msg if {
	not is_dev
	some r in resources_of("azurerm_storage_account")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: storage accounts must not accept public network access outside dev. Receipts are personal and financial data. Deploy from the self-hosted runner inside the VNet instead.",
		[r.address],
	)
}

deny contains msg if {
	is_dev
	some r in resources_of("azurerm_storage_account")
	after(r).public_network_access_enabled == true
	not storage_network_rules_locked_down(r)
	msg := sprintf(
		"%s: storage account allows public network access without a locked-down network_rules -- default_action must be 'Deny' and ip_rules must list specific allowed addresses, not be left open or empty.",
		[r.address],
	)
}

storage_network_rules_locked_down(r) if {
	some rules in after(r).network_rules
	rules.default_action == "Deny"
	count([ip | some ip in rules.ip_rules]) > 0
}

# No Service Bus module exists in this codebase yet, but the same rule
# applies the moment one does -- azurerm_servicebus_namespace has its own
# network_rule_set block, same shape as Storage's network_rules.
deny contains msg if {
	not is_dev
	some r in resources_of("azurerm_servicebus_namespace")
	after(r).public_network_access_enabled == true
	msg := sprintf(
		"%s: Service Bus must not accept public network access outside dev. Reach it over the private endpoint from the app subnet.",
		[r.address],
	)
}

deny contains msg if {
	is_dev
	some r in resources_of("azurerm_servicebus_namespace")
	after(r).public_network_access_enabled == true
	not servicebus_network_rules_locked_down(r)
	msg := sprintf(
		"%s: Service Bus allows public network access without a locked-down network_rule_set -- default_action must be 'Deny' and ip_rules must list specific allowed addresses, not be left open or empty.",
		[r.address],
	)
}

servicebus_network_rules_locked_down(r) if {
	some ruleset in after(r).network_rule_set
	ruleset.default_action == "Deny"
	count([ip | some ip in ruleset.ip_rules]) > 0
}

# uat and prd don't just forbid public access -- they require the private
# endpoint actually be there. Same unknown-until-apply problem as
# covered_by_diagnostics below (the endpoint's target is a resource created
# in the same plan, so private_connection_resource_id is absent from
# `after`), same config-tree fix: match on what the private endpoint's
# expression statically references, not on the resolved id.
requires_private_endpoint := {
	"azurerm_mssql_server",
	"azurerm_key_vault",
	"azurerm_storage_account",
	"azurerm_servicebus_namespace",
}

deny contains msg if {
	not is_dev
	some kind in requires_private_endpoint
	some r in resources_of(kind)
	not has_private_endpoint(r)
	msg := sprintf(
		"%s: no private endpoint found for this resource outside dev. uat and prd must reach every data service over its private endpoint, not the network_acls/network_rules exception dev uses.",
		[r.address],
	)
}

has_private_endpoint(r) if {
	some pe in resources_of("azurerm_private_endpoint")
	some ref in private_endpoint_config_refs[instance_address(pe.address)]
	target_address(object.get(pe, "module_address", ""), ref) == r.address
}

# Every private endpoint here is `count = var.use_private_endpoints ? 1 : 0`
# (modules/keyvault, modules/sql, modules/storage), so its resource_changes
# address carries an instance key ("...azurerm_private_endpoint.this[0]")
# that the static config address it's matched against never has. Strips it.
instance_address(addr) := base if {
	contains(addr, "[")
	base := substring(addr, 0, indexof(addr, "["))
}

instance_address(addr) := addr if {
	not contains(addr, "[")
}

# Full address of every azurerm_private_endpoint's config, mapped to the set
# of resource addresses its private_service_connection.private_connection_
# resource_id expression references. Reuses target_address/config_address/
# module_names below, which were built for the same problem in
# covered_by_diagnostics.
private_endpoint_config_refs[addr] := refs if {
	walk(input.configuration.root_module, [path, cfg])
	is_object(cfg)
	cfg.type == "azurerm_private_endpoint"
	some psc in cfg.expressions.private_service_connection
	refs := psc.private_connection_resource_id.references
	addr := config_address(path, cfg.address)
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

warn contains msg if {
	some kind in required_diagnostics
	some r in resources_of(kind)
	not covered_by_diagnostics(r)
	msg := sprintf(
		"%s: no diagnostic setting found sending logs to Log Analytics.",
		[r.address],
	)
}

# `target_resource_id` on a freshly-created azurerm_monitor_diagnostic_setting
# is the .id of a resource that is *also* being created in this plan, so it is
# unknown-until-apply: `after(r).target_resource_id` is simply absent for
# every diagnostic setting in a from-scratch plan, and matching against it
# (or against `after(r).name`, which has the same problem one hop removed)
# silently fails for every resource, every time. What plan-time *does* know,
# regardless of whether the referenced value itself is known yet, is the
# static config: which resource address the `target_resource_id` expression
# refers to. That lives in `input.configuration`, keyed by module path rather
# than by resolved value, so it's available on every plan.
covered_by_diagnostics(r) if {
	some d in resources_of("azurerm_monitor_diagnostic_setting")
	some ref in diagnostic_config_refs[d.address]
	target_address(object.get(d, "module_address", ""), ref) == r.address
}

# Full address (as it appears in resource_changes[].address) of every
# azurerm_monitor_diagnostic_setting's config, mapped to the set of resource
# addresses its `target_resource_id` expression references. `walk` traverses
# `configuration.root_module` at whatever module depth it's nested to, so
# this doesn't need bespoke recursion to follow module_calls.
diagnostic_config_refs[addr] := refs if {
	walk(input.configuration.root_module, [path, cfg])
	is_object(cfg)
	cfg.type == "azurerm_monitor_diagnostic_setting"
	refs := cfg.expressions.target_resource_id.references
	addr := config_address(path, cfg.address)
}

# `references` lists both the attribute reference ("azurerm_x.y.id") and the
# bare resource reference ("azurerm_x.y") for the same expression -- the
# latter, prefixed with the referencing resource's own module address, is
# exactly the target resource's `resource_changes[].address`. Shared by
# has_private_endpoint above and covered_by_diagnostics here -- both resolve
# a plan-time-unknown `.id` reference back to the resource it targets.
target_address(module_addr, ref) := ref if {
	module_addr == ""
}

target_address(module_addr, ref) := sprintf("%s.%s", [module_addr, ref]) if {
	module_addr != ""
}

# Reconstructs a config-tree resource's full address from the walk() path
# that led to it, by collecting the module name following every
# "module_calls" segment (["module_calls", "app_service", "module", ...] ->
# "module.app_service"), then prefixing the resource's module-relative
# address with it. A root-module resource has no such segments.
config_address(path, local_addr) := local_addr if {
	module_names(path) == []
}

config_address(path, local_addr) := addr if {
	names := module_names(path)
	count(names) > 0
	prefix := concat(".", [sprintf("module.%s", [n]) | some n in names])
	addr := sprintf("%s.%s", [prefix, local_addr])
}

module_names(path) := [name |
	some i, seg in path
	seg == "module_calls"
	name := path[i + 1]
]

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
