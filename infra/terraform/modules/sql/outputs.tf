output "server_id" {
  description = "SQL logical server resource id."
  value       = azurerm_mssql_server.this.id
}

output "server_name" {
  description = "SQL logical server name."
  value       = azurerm_mssql_server.this.name
}

output "server_fqdn" {
  description = "Fully qualified domain name of the SQL server, e.g. for scripts/create-app-user.ps1."
  value       = azurerm_mssql_server.this.fully_qualified_domain_name
}

output "database_id" {
  description = "SQL database resource id."
  value       = azurerm_mssql_database.this.id
}

output "database_name" {
  description = "SQL database name."
  value       = azurerm_mssql_database.this.name
}

output "connection_uri" {
  description = "Credential-free SQL connection string. Authentication is by the app's system-assigned Managed Identity -- no password. Requires the contained database user created by scripts/create-app-user.ps1|.sh after apply."

  # "Column Encryption Setting=Enabled" is not optional here. Beneficiary.
  # BankAccountNumber is Always Encrypted, and without this the driver never
  # engages the Key Vault key store provider registered in Program.cs: reads
  # return raw varbinary ciphertext that fails to materialise into a string,
  # and writes store plaintext into a column declared encrypted. Neither is
  # a connection failure, so both surface far from the cause. appsettings.
  # json's local-development string already carries it; this one must match.
  value = "Server=tcp:${azurerm_mssql_server.this.fully_qualified_domain_name},1433;Database=${azurerm_mssql_database.this.name};Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;Column Encryption Setting=Enabled;"
}
