-- ============================================================================
-- Desicon Finance Workflow -- reset request data before UAT
--
-- Deletes every request and everything hanging off one, and restarts the
-- request-number sequences. Keeps the org chart: Employees, Departments,
-- Beneficiaries, Delegations and SecurityEvents all survive.
--
-- WHY
-- ---
-- Dev accumulated requests from a day of debugging: claims driven half-way,
-- a version-1 orphan (EXP-2026-000005) stranded in AUTHORISATION -- a state
-- no published definition declares any more, so opening it throws -- and
-- several claims raised against the wrong beneficiary while that defect was
-- being found.
--
-- A UAT database where every row was raised by a real person doing a real
-- task is worth more than one that has to be explained. It is also the only
-- way "does this look right to you?" is a meaningful question to put to
-- Finance.
--
-- WHAT THIS DOES NOT REMOVE
-- -------------------------
-- Blobs. The attachments container carries a time-based immutability policy,
-- so receipts already uploaded cannot be deleted until their retention period
-- expires -- by design. Deleting the Attachments rows leaves those blobs
-- orphaned in storage. Harmless, and a demonstration that WORM is doing what
-- it was provisioned to do rather than merely being configured.
--
-- SAFETY -- REWRITTEN 22 Aug 2026, AND WHY IT HAD TO BE
-- ----------------------------------------------------
-- This guard used to read:
--
--     IF @@SERVERNAME NOT LIKE '%-dev%'
--
-- on the reasoning that dev, uat and prd share the database name
-- (DesiconFinanceWorkflow), so the server name was the safe discriminator.
-- That reasoning was sound while "dev" meant a throwaway environment.
--
-- It stopped being true on 22 Aug 2026, when Desicon decided there would be
-- no separate production environment: this platform runs on
-- finance.desiconapp.com, served by sql-desicon-fw-dev. The server name still
-- contains '-dev'. The guard still passes. It now permits the deletion of
-- every expense claim and cash advance Desicon holds, and reports that it is
-- doing so safely.
--
-- Nothing changed in this file to cause that. A control that was correct was
-- made wrong by a decision taken somewhere else, and it would have announced
-- nothing -- which is the precise failure mode this whole project keeps
-- finding, arriving here through the one door nobody was watching.
--
-- The replacement does not try to infer whether an environment is precious.
-- It cannot know, and the old one only appeared to. Instead it requires the
-- operator to name the server they intend to destroy data on, and refuses
-- unless that name matches exactly. There is no environment in which this
-- runs by accident, and no future rename that can quietly re-arm it.
--
--   . .\scripts\dev-db-connect.ps1
--
--   Invoke-Sqlcmd -ServerInstance "sql-desicon-fw-dev.database.windows.net" `
--     -Database "DesiconFinanceWorkflow" -AccessToken $token `
--     -InputFile "scripts/reset-dev-requests.sql" `
--     -Variable @("ConfirmServer=SQL-DESICON-FW-DEV")
--
-- Omit the variable and Invoke-Sqlcmd refuses to run the batch at all, which
-- is the correct outcome: the guard cannot be bypassed by forgetting it.
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Confirm sysname = N'$(ConfirmServer)';

IF @Confirm <> @@SERVERNAME
BEGIN
    RAISERROR (
        'Refusing to run. This deletes EVERY request on this server. Re-run with -v ConfirmServer="%s" if that is genuinely what you intend.',
        16, 1, @@SERVERNAME);
    RETURN;
END;

DECLARE @Requests INT = (SELECT COUNT(*) FROM Requests);

-- Say it out loud before doing it. An operator who typed the server name from
-- muscle memory still gets one line telling them how much they are about to
-- destroy.
PRINT CONCAT('Server: ', @@SERVERNAME, ' -- deleting ', @Requests, ' request(s).');

BEGIN TRANSACTION;

-- Children first. Ordered by dependency, not alphabetically: a delete that
-- fails half way through leaves a database nobody can reason about, which is
-- why the whole thing is one transaction.
DELETE FROM AdvanceRetirementLinks;
DELETE FROM GlPostingLines;
DELETE FROM Attachments;
DELETE FROM AuditEvents;
DELETE FROM OutboxMessages;
DELETE FROM ExpenseLines;
DELETE FROM AdvanceLines;

-- Table-per-type: the subclass rows hold the FK to Requests, so they go
-- before the base table.
DELETE FROM ExpenseRequests;
DELETE FROM CashAdvanceRequests;
DELETE FROM Requests;

COMMIT TRANSACTION;

-- ── Numbering ───────────────────────────────────────────────────────────────
-- One sequence per (module, year), created lazily on first use. Restarting
-- them means UAT begins at EXP-2026-000001 rather than continuing from
-- today's debugging, which matters only because a claim number is the thing
-- people quote to each other.
DECLARE @sequence SYSNAME;
DECLARE @sql NVARCHAR(MAX);

DECLARE sequences CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM sys.sequences WHERE name LIKE 'Seq_Request_%';

OPEN sequences;
FETCH NEXT FROM sequences INTO @sequence;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER SEQUENCE dbo.' + QUOTENAME(@sequence) + N' RESTART WITH 1;';
    EXEC sp_executesql @sql;
    PRINT CONCAT('  restarted ', @sequence);

    FETCH NEXT FROM sequences INTO @sequence;
END;

CLOSE sequences;
DEALLOCATE sequences;

-- ── What survived ───────────────────────────────────────────────────────────
SELECT
    (SELECT COUNT(*) FROM Requests)      AS Requests,
    (SELECT COUNT(*) FROM AuditEvents)   AS AuditEvents,
    (SELECT COUNT(*) FROM Attachments)   AS Attachments,
    (SELECT COUNT(*) FROM OutboxMessages) AS OutboxMessages,
    (SELECT COUNT(*) FROM Employees)     AS Employees,
    (SELECT COUNT(*) FROM Departments)   AS Departments,
    (SELECT COUNT(*) FROM Beneficiaries) AS Beneficiaries;

PRINT '';
PRINT 'Requests cleared. Org chart kept.';
PRINT 'Anyone who will act in UAT needs an Employees row AND an Entra role --';
PRINT 'a role claim without an employee row gives "No active Employee record".';
