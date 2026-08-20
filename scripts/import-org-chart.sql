-- ============================================================================
-- Desicon Finance Workflow -- import the real org chart
--
-- Loads departments, people and approval lines from a CSV that Desicon
-- maintains. Idempotent: re-running with a corrected file fixes the data
-- rather than duplicating it.
--
-- Called by scripts/import-org-chart.ps1, which resolves every email address
-- against Entra ID first. Do not run this file directly: it expects
-- $(RowsJson) to contain rows whose EntraObjectId has already been verified.
-- An email that is not a real directory account must fail in the script, by
-- name, before anything reaches this database.
--
-- WHAT THIS OWNS
-- --------------
-- Departments, Employees, and the two fields the workflow reads for authority:
--
--   Departments.DepartmentHeadId  -- the ONE approval before Cost Control
--   Employees.LineManagerId       -- no longer an approval step (version 4),
--                                    but still decides who may SEE a request
--                                    and who is copied on an overdue advance
--
-- Both are set to the same person: the Head of Department named in the file.
--
-- WHAT IT DOES NOT OWN
-- --------------------
-- Entra app roles. Cost Control, Treasury, the Accounts Manager and the
-- Director of Finance hold claims, not rows, and are granted by
-- scripts/bootstrap-app-roles.ps1. A person needs BOTH -- a role claim with no
-- employee row produces "No active Employee record is linked to Entra object
-- id ...", which has caught four separate people on this project.
--
-- It also never deletes. Someone dropped from the file is left alone rather
-- than deactivated: leavers are a decision, not a side effect of an import,
-- and a requester silently deactivated mid-claim is a request nobody can move.
-- The script reports them instead.
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Rows NVARCHAR(MAX) = N'$(RowsJson)';

DECLARE @Incoming TABLE (
    Email          NVARCHAR(256) NOT NULL,
    FullName       NVARCHAR(200) NOT NULL,
    EntraObjectId  NVARCHAR(200) NOT NULL,
    DepartmentCode NVARCHAR(20)  NOT NULL,
    DepartmentName NVARCHAR(200) NOT NULL,
    IsHead         BIT           NOT NULL);

INSERT INTO @Incoming (Email, FullName, EntraObjectId, DepartmentCode, DepartmentName, IsHead)
SELECT Email, FullName, EntraObjectId, DepartmentCode, DepartmentName, IsHead
FROM OPENJSON(@Rows)
WITH (
    Email          NVARCHAR(256) '$.Email',
    FullName       NVARCHAR(200) '$.FullName',
    EntraObjectId  NVARCHAR(200) '$.EntraObjectId',
    DepartmentCode NVARCHAR(20)  '$.DepartmentCode',
    DepartmentName NVARCHAR(200) '$.DepartmentName',
    IsHead         BIT           '$.IsHead');

IF NOT EXISTS (SELECT 1 FROM @Incoming)
BEGIN
    RAISERROR ('No rows supplied. Nothing to import.', 16, 1);
    RETURN;
END;

BEGIN TRANSACTION;

-- ── Departments ─────────────────────────────────────────────────────────────
INSERT INTO Departments (Code, Name, IsActive)
SELECT DISTINCT i.DepartmentCode, i.DepartmentName, 1
FROM @Incoming i
WHERE NOT EXISTS (SELECT 1 FROM Departments d WHERE d.Code = i.DepartmentCode);

UPDATE d SET d.Name = x.DepartmentName, d.IsActive = 1
FROM Departments d
JOIN (SELECT DISTINCT DepartmentCode, DepartmentName FROM @Incoming) x
  ON x.DepartmentCode = d.Code;

-- ── People who already exist: relink rather than duplicate ─────────────────
-- Matched on EntraObjectId, never on email. Email is an attribute people
-- change; the directory object id is the identity, and it is what the token
-- presents at sign-in.
UPDATE e
SET e.FullName     = i.FullName,
    e.Email        = i.Email,
    e.DepartmentId = d.Id,
    e.IsActive     = 1
FROM Employees e
JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId
JOIN Departments d ON d.Code = i.DepartmentCode;

-- ── People who do not ───────────────────────────────────────────────────────
-- Staff numbers allocated from the highest free number, not by position: dev
-- already holds DEV-0001..DEV-0010 and a positional scheme collides with them.
DECLARE @NextSeq INT = ISNULL((
    SELECT MAX(TRY_CAST(SUBSTRING(StaffNumber, 5, 10) AS INT))
    FROM Employees
    WHERE StaffNumber LIKE N'DEV-%'), 0) + 1;

INSERT INTO Employees (Id, EntraObjectId, StaffNumber, FullName, Email, DepartmentId, IsActive)
SELECT
    NEWID(),
    n.EntraObjectId,
    N'DEV-' + RIGHT(N'0000' + CAST(@NextSeq - 1 + ROW_NUMBER() OVER (ORDER BY n.EntraObjectId) AS NVARCHAR(10)), 4),
    n.FullName,
    n.Email,
    d.Id,
    1
FROM (
    SELECT i.EntraObjectId,
           MIN(i.FullName)       AS FullName,
           MIN(i.Email)          AS Email,
           MIN(i.DepartmentCode) AS DepartmentCode
    FROM @Incoming i
    WHERE NOT EXISTS (SELECT 1 FROM Employees e WHERE e.EntraObjectId = i.EntraObjectId)
    GROUP BY i.EntraObjectId
) AS n
JOIN Departments d ON d.Code = n.DepartmentCode;

-- ── Heads of Department ─────────────────────────────────────────────────────
-- The single approval before Cost Control. A department with no head recorded
-- has requests that stop dead at the first step, so this is reported below.
UPDATE d
SET d.DepartmentHeadId = e.Id
FROM Departments d
JOIN @Incoming i ON i.DepartmentCode = d.Code AND i.IsHead = 1
JOIN Employees e ON e.EntraObjectId = i.EntraObjectId;

-- ── Reporting lines ─────────────────────────────────────────────────────────
-- Everyone reports to their department's head, and the head reports to nobody
-- here. Not an approval path any more -- ReadAccessScope uses it to decide who
-- may see a request, and the retirement-overdue notification copies it.
UPDATE e
SET e.LineManagerId = d.DepartmentHeadId
FROM Employees e
JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId AND i.IsHead = 0
JOIN Departments d ON d.Code = i.DepartmentCode
WHERE d.DepartmentHeadId IS NOT NULL AND d.DepartmentHeadId <> e.Id;

UPDATE e
SET e.LineManagerId = NULL
FROM Employees e
JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId AND i.IsHead = 1;

COMMIT TRANSACTION;

-- ── What the import produced ────────────────────────────────────────────────
SELECT
    d.Code                        AS Dept,
    d.Name                        AS DepartmentName,
    ISNULL(h.FullName, N'** NONE **') AS HeadOfDepartment,
    h.Email                       AS HeadEmail,
    (SELECT COUNT(*) FROM Employees e WHERE e.DepartmentId = d.Id AND e.IsActive = 1) AS People
FROM Departments d
LEFT JOIN Employees h ON h.Id = d.DepartmentHeadId
WHERE EXISTS (SELECT 1 FROM @Incoming i WHERE i.DepartmentCode = d.Code)
ORDER BY d.Code;

-- ── The two things that stop a request dead ─────────────────────────────────
PRINT '';
PRINT 'Departments with no head -- every request they raise stops at the first approval:';
SELECT d.Code, d.Name
FROM Departments d
WHERE d.DepartmentHeadId IS NULL
  AND EXISTS (SELECT 1 FROM @Incoming i WHERE i.DepartmentCode = d.Code);

PRINT '';
PRINT 'People in the database but NOT in this file -- decide deliberately, this script will not:';
SELECT e.StaffNumber, e.FullName, e.Email
FROM Employees e
WHERE e.IsActive = 1
  AND NOT EXISTS (SELECT 1 FROM @Incoming i WHERE i.EntraObjectId = e.EntraObjectId)
ORDER BY e.StaffNumber;
