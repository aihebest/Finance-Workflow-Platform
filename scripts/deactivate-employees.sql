-- ============================================================================
-- Desicon Finance Workflow -- deactivate people who have left, or fixtures
-- that were never real.
--
-- Sets IsActive = 0. Does not delete: audit events, beneficiaries and past
-- approvals reference employees by id, and a deleted row turns a complete
-- audit trail into one with holes in it. An inactive employee cannot be
-- resolved as an approver and cannot sign in, which is the whole requirement.
--
-- Usage (semicolon-separated emails):
--   Invoke-Sqlcmd ... -InputFile scripts/deactivate-employees.sql `
--     -Variable "Emails=dev.linemanager@desicongroup.com;wazuhalerts@desicongroup.com"
--
-- REFUSES rather than breaks things
-- ---------------------------------
-- Two checks, both learned the hard way on this project:
--
--   1. A Head of Department. Deactivating one leaves every request their
--      department raises stopping at the first approval, with nobody able to
--      move it and nothing on screen explaining why. Record a replacement head
--      first, then run this.
--
--   2. Anyone who is the current actor on an open request. The request would
--      be waiting on a person the system no longer recognises -- the same
--      stranding that lost EXP-2026-000004 for weeks.
--
-- Both are reported and the whole batch is refused. Deactivating half a list
-- and failing on the rest is worse than doing nothing, because nobody can tell
-- afterwards which half went through.
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Emails NVARCHAR(MAX) = N'$(Emails)';

DECLARE @Targets TABLE (Email NVARCHAR(256) NOT NULL);

INSERT INTO @Targets (Email)
SELECT LTRIM(RTRIM(value)) FROM STRING_SPLIT(@Emails, ';')
WHERE LTRIM(RTRIM(value)) <> N'';

IF NOT EXISTS (SELECT 1 FROM @Targets)
BEGIN
    RAISERROR ('No emails supplied.', 16, 1);
    RETURN;
END;

-- ── Anyone named who does not exist ─────────────────────────────────────────
IF EXISTS (SELECT 1 FROM @Targets t WHERE NOT EXISTS (
              SELECT 1 FROM Employees e WHERE e.Email = t.Email))
BEGIN
    PRINT 'Not found in Employees:';
    SELECT t.Email FROM @Targets t
    WHERE NOT EXISTS (SELECT 1 FROM Employees e WHERE e.Email = t.Email);

    RAISERROR ('One or more addresses do not match an employee. Nothing changed.', 16, 1);
    RETURN;
END;

-- ── Check 1: heads of department ────────────────────────────────────────────
IF EXISTS (SELECT 1
           FROM Departments d
           JOIN Employees e ON e.Id = d.DepartmentHeadId
           JOIN @Targets t ON t.Email = e.Email)
BEGIN
    PRINT 'These people head a department. Record a replacement head first:';
    SELECT e.FullName, e.Email, d.Code AS Heads, d.Name
    FROM Departments d
    JOIN Employees e ON e.Id = d.DepartmentHeadId
    JOIN @Targets t ON t.Email = e.Email;

    RAISERROR ('Refused: deactivating a head of department stops every request it raises. Nothing changed.', 16, 1);
    RETURN;
END;

-- ── Check 2: currently holding an open request ──────────────────────────────
IF EXISTS (SELECT 1
           FROM Requests r
           JOIN Employees e ON e.Id = r.CurrentActorId
           JOIN @Targets t ON t.Email = e.Email
           WHERE r.ClosedAt IS NULL)
BEGIN
    PRINT 'These people are the current approver on open requests:';
    SELECT e.FullName, e.Email, r.RequestNumber, r.CurrentState
    FROM Requests r
    JOIN Employees e ON e.Id = r.CurrentActorId
    JOIN @Targets t ON t.Email = e.Email
    WHERE r.ClosedAt IS NULL;

    RAISERROR ('Refused: those requests would be waiting on somebody the system no longer recognises. Nothing changed.', 16, 1);
    RETURN;
END;

-- ── Deactivate ──────────────────────────────────────────────────────────────
BEGIN TRANSACTION;

UPDATE e SET e.IsActive = 0
FROM Employees e
JOIN @Targets t ON t.Email = e.Email;

-- Anyone reporting to them now reports to nobody, which is honest. A stale
-- pointer to an inactive manager would resolve to somebody who cannot act.
UPDATE e SET e.LineManagerId = NULL
FROM Employees e
JOIN Employees m ON m.Id = e.LineManagerId
JOIN @Targets t ON t.Email = m.Email;

COMMIT TRANSACTION;

SELECT e.StaffNumber, e.FullName, e.Email, e.IsActive
FROM Employees e
JOIN @Targets t ON t.Email = e.Email
ORDER BY e.StaffNumber;

PRINT '';
PRINT 'Deactivated. Their Entra sign-in and any app roles are untouched --';
PRINT 'revoke those separately, or they keep a claim that grants nothing.';
