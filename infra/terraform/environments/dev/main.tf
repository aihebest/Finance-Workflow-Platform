locals {
  name_prefix = "desicon-fw-${var.environment}"
  app_name    = "app-desicon-fw-api-${var.environment}"

  # Linux Web App hostnames are deterministic (<name>.azurewebsites.net) and the
  # name must be globally unique, so this can be computed without depending on
  # module.app_service. That breaks what would otherwise be a real cycle:
  # frontdoor needs the app's hostname as its origin, and app-service needs
  # frontdoor's resource GUID to lock its ip_restriction down to Front Door only.
  app_default_hostname = "${local.app_name}.azurewebsites.net"

  tags = {
    environment         = var.environment
    owner               = var.owner
    cost_centre         = var.cost_centre
    data_classification = "Confidential"
  }
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.name_prefix}"
  location = var.location
  tags     = local.tags
}

# ── Foundational: monitoring + network (every other module depends on these) ─
module "monitoring" {
  source = "../../modules/monitoring"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  alert_email_addresses = var.alert_email_addresses
  tags                  = local.tags
}

module "network" {
  source = "../../modules/network"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  # Dev drops private endpoints entirely -- see the module blocks below and
  # docs/02-Solution-Architecture.md. No private DNS zones to link either.
  use_private_endpoints = false

  tags = local.tags
}

# ── Data services (private endpoints dropped in dev -- IP-restricted public
#    access instead; see docs/02-Solution-Architecture.md "Environments") ───
module "keyvault" {
  source = "../../modules/keyvault"

  name                = "kv-${local.name_prefix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  tenant_id           = data.azurerm_client_config.current.tenant_id

  # The identity running Terraform must be able to create the SQL TDE key and
  # Storage CMK below -- RBAC grants nothing to the vault's creator implicitly.
  administrator_object_ids = distinct(concat(
    [data.azurerm_client_config.current.object_id],
    var.keyvault_administrator_object_ids,
  ))

  # Dev has no self-hosted runner inside the VNet (see docs/02-Solution-
  # Architecture.md), so the private endpoint alone leaves Terraform with no
  # path to the vault it needs to create the SQL TDE key and Storage CMK in.
  # Drop the private endpoint entirely and fall back to a narrow, IP-pinned
  # exception instead -- not a blanket opening: network_acls stays
  # default_action = Deny, only deployer_ip_addresses is admitted.
  use_private_endpoints         = false
  public_network_access_enabled = true
  ip_rules                      = var.deployer_ip_addresses

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "storage" {
  source = "../../modules/storage"

  name                = replace("st${local.name_prefix}", "-", "")
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  key_vault_id = module.keyvault.id

  # Dev has no self-hosted runner inside the VNet -- same narrow, IP-pinned
  # exception as Key Vault (see that module call above).
  use_private_endpoints         = false
  public_network_access_enabled = true
  ip_rules                      = var.deployer_ip_addresses

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

module "sql" {
  source = "../../modules/sql"

  name                = "sql-${local.name_prefix}"
  database_name       = "DesiconFinanceWorkflow"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location

  sku_name       = var.sql_sku_name
  zone_redundant = false # prd only, per policy/terraform/azure_security.rego

  key_vault_id = module.keyvault.id

  entra_admin_login     = var.entra_admin_login
  entra_admin_object_id = var.entra_admin_object_id

  # Dev has no self-hosted runner inside the VNet -- same narrow, IP-pinned
  # exception as Key Vault and the attachments storage account above.
  use_private_endpoints         = false
  public_network_access_enabled = true
  ip_rules                      = var.deployer_ip_addresses

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

# ── Front Door (created before app-service so app-service can lock its
#    ip_restriction to frontdoor's resource GUID) ───────────────────────────
module "frontdoor" {
  source = "../../modules/frontdoor"

  name                = local.name_prefix
  resource_group_name = azurerm_resource_group.main.name

  origin_hostname   = local.app_default_hostname
  health_probe_path = "/health/ready"

  log_analytics_workspace_id = module.monitoring.log_analytics_workspace_id
  tags                       = local.tags
}

# ── Compute ──────────────────────────────────────────────────────────────
module "app_service" {
  source = "../../modules/app-service"

  name                = local.app_name
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  environment         = var.environment

  sku_name       = var.app_service_sku_name
  zone_redundant = false # prd only
  worker_count   = 1     # dev: single instance

  subnet_id                     = module.network.app_subnet_id
  public_network_access_enabled = true # fronted by Front Door
  front_door_id                 = module.frontdoor.frontdoor_id

  container_image        = var.container_image
  container_registry_url = var.container_registry_url
  container_registry_id  = var.container_registry_id

  key_vault_id       = module.keyvault.id
  key_vault_uri      = module.keyvault.uri
  storage_account_id = module.storage.id
  sql_connection_uri = module.sql.connection_uri

  # Explicit, not inferred from *_id nullness: both ids above are module
  # outputs (unknown until apply), and a count/for_each keyed off "is this
  # unknown value null" is exactly what broke plan before. dev always
  # provisions its own storage account and Key Vault, so both stay true;
  # container_registry_id is a root variable, so its presence is knowable
  # at plan time and can safely gate enable_acr_role_assignment.
  enable_keyvault_role_assignment = true
  enable_storage_role_assignment  = true
  enable_acr_role_assignment      = var.container_registry_id != null

  app_insights_connection_string = module.monitoring.app_insights_connection_string
  log_analytics_workspace_id     = module.monitoring.log_analytics_workspace_id

  entra_client_id      = var.entra_client_id
  tenant_id            = data.azurerm_client_config.current.tenant_id
  allowed_cors_origins = var.allowed_cors_origins

  enable_staging_slot = false # dev: no swap workflow

  tags = local.tags
}

module "functions" {
  source = "../../modules/functions"

  name                 = "func-${local.name_prefix}"
  storage_account_name = replace("st${local.name_prefix}fn", "-", "")
  # stdesiconfwdevfn has 404'd on the blob data plane on four straight applies
  # despite ARM reporting it Succeeded -- abandon that name for a fresh one
  # rather than keep retrying against a possibly permanently wedged account.
  storage_name_suffix = "2"
  resource_group_name = azurerm_resource_group.main.name
  location            = var.location
  environment         = var.environment

  sku_name = var.functions_sku_name

  app_subnet_id = module.network.app_subnet_id

  # Dev has no self-hosted runner inside the VNet -- same narrow, IP-pinned
  # exception as Key Vault and the attachments storage account above.
  use_private_endpoints                 = false
  storage_public_network_access_enabled = true
  storage_ip_rules                      = var.deployer_ip_addresses

  key_vault_id       = module.keyvault.id
  key_vault_uri      = module.keyvault.uri
  sql_connection_uri = module.sql.connection_uri

  app_insights_connection_string = module.monitoring.app_insights_connection_string
  log_analytics_workspace_id     = module.monitoring.log_analytics_workspace_id

  tags = local.tags
}

resource "azurerm_key_vault_key" "beneficiary_bank_details_cmk" {
  name         = "cmk-beneficiary-bank-details"
  key_vault_id = module.keyvault.id
  key_type     = "RSA-HSM"
  key_size     = 3072
  key_opts     = ["wrapKey", "unwrapKey", "sign", "verify"]

  tags = local.tags
}