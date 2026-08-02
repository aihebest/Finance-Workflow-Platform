-- Pre-creates the per-module, per-year request-number sequences and grants
-- the application identities the UPDATE permission that NEXT VALUE FOR
-- requires.
--
-- WHY THIS FILE EXISTS
-- --------------------
-- SqlSequenceRequestNumberGenerator creates its sequences lazily at runtime
-- (CREATE SEQUENCE on first use for a given module/year). That is DDL, and
-- the application identities deliberately hold only db_datareader and
-- db_datawriter -- see create-app-user.sql, which states outright that the
-- app has "no schema-modification rights". The two designs contradict each
-- other: the lazy-create path cannot succeed under the intended privileges.
--
-- Separately, db_datawriter does NOT cover sequences. NEXT VALUE FOR needs
-- an explicit UPDATE grant on the sequence object, so even a pre-created
-- sequence is unusable without the grants below.
--
-- This script resolves both by provisioning ahead of time under an admin
-- identity. It is idempotent and safe to re-run.
--
-- KNOWN LIMITATION: this covers a fixed window of years. When the window
-- lapses, request numbering fails at runtime. The durable fix is either an
-- EF migration that creates each year's sequences, or a timer function that
-- provisions next year's sequences in December -- see 12-Decision-Log.md.

SET NOCOUNT ON;

DECLARE @Modules TABLE (ModuleKey SYSNAME);
INSERT INTO @Modules (ModuleKey) VALUES (N'CASH_ADVANCE'), (N'EXPENSE'), (N'LEAVE_REQUEST');

DECLARE @Principals TABLE (PrincipalName SYSNAME);
INSERT INTO @Principals (PrincipalName) VALUES (N'$(ApiPrincipal)'), (N'$(FunctionPrincipal)');

DECLARE @StartYear INT = YEAR(SYSUTCDATETIME());
DECLARE @EndYear   INT = YEAR(SYSUTCDATETIME()) + CONVERT(INT, N'$(YearsAhead)');

DECLARE @Year INT = @StartYear;

WHILE @Year <= @EndYear
BEGIN
    DECLARE @ModuleKey SYSNAME;
    DECLARE ModuleCursor CURSOR LOCAL FAST_FORWARD FOR SELECT ModuleKey FROM @Modules;
    OPEN ModuleCursor;
    FETCH NEXT FROM ModuleCursor INTO @ModuleKey;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @SeqName SYSNAME = N'Seq_Request_' + @ModuleKey + N'_' + CONVERT(NVARCHAR(4), @Year);
        DECLARE @Sql NVARCHAR(MAX);

        IF NOT EXISTS (
            SELECT 1 FROM sys.sequences
            WHERE name = @SeqName AND schema_id = SCHEMA_ID(N'dbo')
        )
        BEGIN
            SET @Sql = N'CREATE SEQUENCE dbo.' + QUOTENAME(@SeqName)
                     + N' AS BIGINT START WITH 1 INCREMENT BY 1 NO CACHE;';
            EXEC sp_executesql @Sql;
            PRINT 'Created sequence dbo.' + @SeqName;
        END
        ELSE
        BEGIN
            PRINT 'Sequence dbo.' + @SeqName + ' already exists, skipping create.';
        END

        DECLARE @Principal SYSNAME;
        DECLARE PrincipalCursor CURSOR LOCAL FAST_FORWARD FOR SELECT PrincipalName FROM @Principals;
        OPEN PrincipalCursor;
        FETCH NEXT FROM PrincipalCursor INTO @Principal;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Principal)
            BEGIN
                SET @Sql = N'GRANT UPDATE ON OBJECT::dbo.' + QUOTENAME(@SeqName)
                         + N' TO ' + QUOTENAME(@Principal) + N';';
                EXEC sp_executesql @Sql;
            END
            ELSE
            BEGIN
                PRINT 'WARNING: principal ' + @Principal + ' does not exist -- grant skipped.';
            END

            FETCH NEXT FROM PrincipalCursor INTO @Principal;
        END

        CLOSE PrincipalCursor;
        DEALLOCATE PrincipalCursor;

        FETCH NEXT FROM ModuleCursor INTO @ModuleKey;
    END

    CLOSE ModuleCursor;
    DEALLOCATE ModuleCursor;

    SET @Year = @Year + 1;
END

PRINT 'Sequence provisioning complete.';
GO

-- Verification: every sequence should show an UPDATE grant per app principal.
SELECT s.name                AS sequence_name,
       dp.name               AS grantee,
       p.permission_name,
       p.state_desc
FROM sys.sequences s
LEFT JOIN sys.database_permissions p
       ON p.major_id = s.object_id
LEFT JOIN sys.database_principals dp
       ON dp.principal_id = p.grantee_principal_id
ORDER BY s.name, dp.name;
