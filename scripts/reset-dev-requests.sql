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
-- SAFETY
-- ------
-- Refuses to run anywhere but a server whose name contains '-dev'. dev, uat
-- and prd share the database NAME (DesiconFinanceWorkflow), so the database
-- name is not a safe discriminator and the server name is.
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @@SERVERNAME NOT LIKE '%-dev%'
BEGIN
    RAISERROR (
        'Refusing to run: this script deletes every request and is intended for dev only. Server is %s.',
        16, 1, @@SERVERNAME);
    RETURN;
END;

DECLARE @Requests INT = (SELECT COUNT(*) FROM Requests);
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
