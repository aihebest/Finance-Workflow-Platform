-- Creates a contained database user for an Entra ID (managed identity)
-- principal and grants it the application's data-access role membership.
--
-- Must run AFTER Terraform apply, connected AS AN ENTRA ADMIN (see the
-- server's azuread_administrator block) -- Terraform's azurerm provider
-- cannot run CREATE USER ... FROM EXTERNAL PROVIDER itself, since that
-- requires an authenticated connection to the database, not the ARM API.
--
-- Invoke via create-app-user.ps1 or create-app-user.sh, which substitute
-- $(AppName) and $(DatabaseName) and run this through sqlcmd/Invoke-Sqlcmd.
-- Do not run this file directly with literal values -- use the wrapper
-- scripts so the same file works for the App Service and Function App
-- identities, and for every environment.

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(AppName)')
BEGIN
    CREATE USER [$(AppName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created contained user [$(AppName)].';
END
ELSE
BEGIN
    PRINT 'User [$(AppName)] already exists, skipping create.';
END

-- Least-privilege application role. The workflow engine reads and writes
-- its own tables but has no schema-modification rights -- migrations run
-- under a separate, higher-privileged identity (see Migrations/README or
-- the CI deploy job), never under the app's own managed identity.
IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members rm
    JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
    WHERE r.name = N'db_datareader' AND m.name = N'$(AppName)'
)
BEGIN
    ALTER ROLE db_datareader ADD MEMBER [$(AppName)];
END

IF NOT EXISTS (
    SELECT 1
    FROM sys.database_role_members rm
    JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
    JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
    WHERE r.name = N'db_datawriter' AND m.name = N'$(AppName)'
)
BEGIN
    ALTER ROLE db_datawriter ADD MEMBER [$(AppName)];
END

-- Always Encrypted metadata, required to WRITE Beneficiary.BankAccountNumber.
--
-- db_datawriter is not sufficient. Encrypting a parameter makes the client
-- call sp_describe_parameter_encryption to learn which key protects the
-- column, and that procedure requires both permissions below. Without them
-- the insert fails and the application answers 500.
--
-- Reads are unaffected, which is what makes this expensive to find: the
-- readiness probe selects the encrypted column successfully every thirty
-- seconds while every write fails. The integration suite never sees it
-- either, because Testcontainers connects as sa and holds every permission
-- implicitly -- so the tests, the probe and the deployment all look healthy.
--
-- These are metadata permissions, not data ones: they permit reading the key
-- *definitions*, not decrypting anything. The plaintext key never leaves Key
-- Vault, and access to it is the separate Crypto User role assignment on the
-- app's managed identity.
GRANT VIEW ANY COLUMN ENCRYPTION KEY DEFINITION TO [$(AppName)];
GRANT VIEW ANY COLUMN MASTER KEY DEFINITION TO [$(AppName)];
