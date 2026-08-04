###############################################################################
# Desicon Finance Workflow Platform — Network module
#
# Hub for the whole environment: one VNet, an app subnet (VNet-integrated
# App Service + Functions) and a private-endpoint subnet for every data
# service. NSGs restrict egress from the app subnet to the specific Azure
# service tags the platform actually calls -- see docs/04-Security-and-
# DevSecOps.md §2, "No unrestricted outbound". Private DNS zones for the
# data-plane services are created here (not per-module) because they are
# one-per-VNet resources shared by every module that needs a private
# endpoint; creating them per-module would race on the same zone name.
#
# Usage:
#   module "network" {
#     source              = "../../modules/network"
#     name_prefix         = "desicon-fw-dev"
#     resource_group_name = azurerm_resource_group.main.name
#     location            = var.location
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
  private_dns_zone_names = {
    sql       = "privatelink.database.windows.net"
    key_vault = "privatelink.vaultcore.azure.net"
    blob      = "privatelink.blob.core.windows.net"
    queue     = "privatelink.queue.core.windows.net"
    table     = "privatelink.table.core.windows.net"
  }
}

resource "azurerm_virtual_network" "this" {
  name                = "vnet-${var.name_prefix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  address_space       = var.address_space

  tags = var.tags
}

# ── App subnet: VNet integration target for App Service and Functions ──────
resource "azurerm_subnet" "app" {
  name                 = "snet-app"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = var.app_subnet_address_prefixes

  # Service endpoints are what make a subnet nameable in another resource's
  # network rules. A Key Vault network_acls entry or a SQL virtual network
  # rule that references a subnet lacking the matching endpoint is accepted
  # by Azure and then silently never matches -- so the symptom is a timeout,
  # not a configuration error. Dev needs both because it has no private
  # endpoints: see modules/keyvault (network_acl_subnet_ids) and modules/sql
  # (vnet_rule_subnet_ids).
  service_endpoints = ["Microsoft.KeyVault", "Microsoft.Sql", "Microsoft.Storage"]

  # Regional VNet Integration requires an empty subnet delegated to
  # Microsoft.Web/serverFarms. App Service and the Functions Premium plan
  # can share one delegated subnet in the same region.
  delegation {
    name = "webapp-delegation"
    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

# ── Private endpoint subnet: SQL, Key Vault, Storage land here ─────────────
resource "azurerm_subnet" "pe" {
  name                 = "snet-pe"
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = var.pe_subnet_address_prefixes

  # Private endpoints require network policies (NSG/route table enforcement
  # on the PE's own IP) to be disabled on the subnet that hosts them.
  private_endpoint_network_policies = "Disabled"
}

# ── NSG: app subnet may only egress to the specific services it calls ──────
resource "azurerm_network_security_group" "app" {
  name                = "nsg-${var.name_prefix}-app"
  resource_group_name = var.resource_group_name
  location            = var.location

  security_rule {
    name                       = "AllowVNetOutbound"
    priority                   = 100
    direction                  = "Outbound"
    access                     = "Allow"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "VirtualNetwork"
    destination_address_prefix = "VirtualNetwork"
    description                = "Traffic to private endpoints in snet-pe (SQL, Key Vault, Storage) stays inside the VNet."
  }

  security_rule {
    name                       = "AllowAzureActiveDirectoryOutbound"
    priority                   = 110
    direction                  = "Outbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "443"
    source_address_prefix      = "*"
    destination_address_prefix = "AzureActiveDirectory"
    description                = "Entra ID token validation and Microsoft Graph mail send."
  }

  security_rule {
    name                       = "AllowAzureMonitorOutbound"
    priority                   = 120
    direction                  = "Outbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "443"
    source_address_prefix      = "*"
    destination_address_prefix = "AzureMonitor"
    description                = "Application Insights / Log Analytics telemetry ingestion."
  }

  # ── Service-endpoint destinations (dev only) ─────────────────────────────
  # When use_private_endpoints is true, SQL and Key Vault are private
  # endpoints inside the VNet and AllowVNetOutbound above already covers
  # them. Dev has no private endpoints: it reaches both at their public
  # FQDNs over service endpoints, and NSG classifies that traffic under the
  # Sql and AzureKeyVault service tags -- which are subsets of Internet, so
  # DenyInternetOutbound below swallows it.
  #
  # The symptom is a TCP timeout ("error: 40 - Could not open a connection
  # to SQL Server") roughly 85 seconds in, with the SQL firewall, the VNet
  # rule and the service endpoint all correctly configured. Nothing in the
  # SQL configuration is wrong; the packets never leave the subnet.
  dynamic "security_rule" {
    for_each = var.use_private_endpoints ? [] : [1]
    content {
      name                       = "AllowSqlOutbound"
      priority                   = 130
      direction                  = "Outbound"
      access                     = "Allow"
      protocol                   = "Tcp"
      source_port_range          = "*"
      source_address_prefix      = "*"
      destination_address_prefix = "Sql"

      # 1433 alone is not enough. Azure SQL's connection policy for clients
      # inside Azure defaults to Redirect: the client opens 1433 to the
      # gateway, the gateway hands back a node address on a port in
      # 11000-11999, and the real session runs there. Allowing only 1433
      # lets the handshake start and then drops the redirect, which
      # surfaces as "error: 40 - Could not open a connection" -- identical
      # to having no rule at all, just faster.
      #
      # Forcing the server to Proxy policy would avoid this at the cost of
      # routing every query through the gateway; Redirect plus this range
      # is the documented arrangement.
      destination_port_ranges = ["1433", "11000-11999"]

      # Azure caps security_rule.description at 140 characters.
      description = "Azure SQL via the Microsoft.Sql service endpoint, including the Redirect port range. Dev only -- uat/prd use a private endpoint."
    }
  }

  dynamic "security_rule" {
    for_each = var.use_private_endpoints ? [] : [1]
    content {
      name                       = "AllowKeyVaultOutbound"
      priority                   = 140
      direction                  = "Outbound"
      access                     = "Allow"
      protocol                   = "Tcp"
      source_port_range          = "*"
      destination_port_range     = "443"
      source_address_prefix      = "*"
      destination_address_prefix = "AzureKeyVault"
      description                = "Unwrapping the Always Encrypted column master key. Dev only -- uat/prd reach Key Vault over a private endpoint."
    }
  }

  dynamic "security_rule" {
    for_each = var.use_private_endpoints ? [] : [1]
    content {
      name                       = "AllowStorageOutbound"
      priority                   = 150
      direction                  = "Outbound"
      access                     = "Allow"
      protocol                   = "Tcp"
      source_port_range          = "*"
      destination_port_range     = "443"
      source_address_prefix      = "*"
      destination_address_prefix = "Storage"
      description                = "Functions runtime storage and the run-from-package blob, via the Microsoft.Storage service endpoint. Dev only."
    }
  }

  security_rule {
    name                       = "DenyInternetOutbound"
    priority                   = 4000
    direction                  = "Outbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "*"
    destination_address_prefix = "Internet"
    description                = "No unrestricted outbound. Every legitimate destination has an explicit allow rule above."
  }

  tags = var.tags
}

resource "azurerm_subnet_network_security_group_association" "app" {
  subnet_id                 = azurerm_subnet.app.id
  network_security_group_id = azurerm_network_security_group.app.id
}

# ── NSG: private endpoint subnet only accepts traffic from the app subnet ──
resource "azurerm_network_security_group" "pe" {
  name                = "nsg-${var.name_prefix}-pe"
  resource_group_name = var.resource_group_name
  location            = var.location

  security_rule {
    name                       = "AllowAppSubnetInbound"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = var.app_subnet_address_prefixes[0]
    destination_address_prefix = "VirtualNetwork"
    description                = "Only the app subnet may reach private endpoints."
  }

  security_rule {
    name                       = "DenyAllOtherInbound"
    priority                   = 4000
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }

  tags = var.tags
}

resource "azurerm_subnet_network_security_group_association" "pe" {
  subnet_id                 = azurerm_subnet.pe.id
  network_security_group_id = azurerm_network_security_group.pe.id
}

# ── Private DNS zones, one per data-plane service, linked to this VNet ─────
# Skipped entirely when use_private_endpoints is false (dev only): a zone
# with no private endpoint to resolve is dead weight, and dev's data
# services resolve their public hostnames instead. See modules/keyvault,
# modules/sql, modules/storage and modules/functions, which each carry the
# same toggle and gate their own azurerm_private_endpoint resources on it.
resource "azurerm_private_dns_zone" "this" {
  for_each            = var.use_private_endpoints ? local.private_dns_zone_names : {}
  name                = each.value
  resource_group_name = var.resource_group_name

  tags = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "this" {
  for_each              = azurerm_private_dns_zone.this
  name                  = "link-${each.key}"
  resource_group_name   = var.resource_group_name
  private_dns_zone_name = each.value.name
  virtual_network_id    = azurerm_virtual_network.this.id
  registration_enabled  = false

  tags = var.tags
}
