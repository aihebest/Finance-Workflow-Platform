-- Creates the contained database user that CI uses to apply migrations.
--
-- WHY A SEPARATE PRINCIPAL
-- ------------------------
-- create-app-user.sql grants the application identities db_datareader and
-- db_datawriter and nothing else, on the stated principle that "the workflow
-- engine reads and writes its own tables but has no schema-modification
-- rights -- migrations run under a separate, higher-privileged identity".
-- This file is that separate identity. Until it existed, the sentence was
-- aspirational: no such principal had been created, and no pipeline applied
-- migrations at all.
--
-- Keeping DDL away from the app identity is not ceremony. The audit tables
-- are append-only and hash-chained; an application that can ALTER TABLE can
-- quietly restructure the evidence of its own behaviour. The separation is
-- only real if the app genuinely cannot do it.
--
-- WHAT IT GRANTS
-- --------------
-- db_ddladmin  -- create and alter tables, indexes, sequences
-- db_datareader / db_datawriter -- EF migrations read and write
--                 __EFMigrationsHistory, and data migrations touch rows
-- ALTER ANY COLUMN ENCRYPTION KEY / MASTER KEY -- the Always Encrypted
--                 migration reads sys.column_master_keys; provisioning the
--                 keys themselves stays manual and out of CI, because
--                 wrapping a CEK needs Key Vault and a considered decision
--                 per environment (see Provision-AlwaysEncryptedKeys.ps1).
--
-- Deliberately NOT db_owner: that would include the ability to drop the
-- database, alter role membership, and disable auditing.
--
-- Run once per environment, as an Entra admin, with $(PrincipalName) set to
-- the display name of the Entra application CI federates as.

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$(PrincipalName)')
BEGIN
    CREATE USER [$(PrincipalName)] FROM EXTERNAL PROVIDER;
    PRINT 'Created contained user [$(PrincipalName)].';
END
ELSE
BEGIN
    PRINT 'User [$(PrincipalName)] already exists, skipping create.';
END

DECLARE @roles TABLE (RoleName SYSNAME);
INSERT INTO @roles (RoleName) VALUES (N'db_ddladmin'), (N'db_datareader'), (N'db_datawriter');

DECLARE @role SYSNAME;
DECLARE RoleCursor CURSOR LOCAL FAST_FORWARD FOR SELECT RoleName FROM @roles;
OPEN RoleCursor;
FETCH NEXT FROM RoleCursor INTO @role;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM sys.database_role_members rm
        JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
        JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
        WHERE r.name = @role AND m.name = N'$(PrincipalName)'
    )
    BEGIN
        DECLARE @sql NVARCHAR(MAX) =
            N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(N'$(PrincipalName)') + N';';
        EXEC sp_executesql @sql;
        PRINT 'Granted ' + @role + '.';
    END

    FETCH NEXT FROM RoleCursor INTO @role;
END

CLOSE RoleCursor;
DEALLOCATE RoleCursor;

-- Read-only visibility of the Always Encrypted key metadata. VIEW ANY is not
-- available at database scope for these, so grant the specific permissions.
GRANT VIEW ANY COLUMN ENCRYPTION KEY DEFINITION TO [$(PrincipalName)];
GRANT VIEW ANY COLUMN MASTER KEY DEFINITION TO [$(PrincipalName)];

PRINT 'Migration principal [$(PrincipalName)] configured.';
