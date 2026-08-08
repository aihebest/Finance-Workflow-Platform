-- Seeds enough of an org chart for one request to travel end to end.
--
-- WHY THIS IS NEEDED
-- ------------------
-- seed-dev-employee.sql creates one person and says so plainly: "No
-- LineManagerId: this person is the top of the tree in dev." That is correct
-- for proving sign-in works, and insufficient for anything else. A claim
-- submitted by the only employee lands in LINE_MANAGER, LineManagerOf
-- resolves to nobody, and the request is stranded with no actor and no error
-- -- the API is behaving exactly as designed, and the claim can never move.
--
-- The integration suite never sees this because WorkflowSteps.CreateOrgChartAsync
-- builds a six-person chart per test and Respawn deletes it afterwards. Dev
-- has never had more than one row in Employees.
--
-- WHAT THIS DOES NOT DO
-- ---------------------
-- Roles. FinanceOfficer and FinanceManager are Entra app roles read from the
-- token's "roles" claim (CurrentUserAccessor.GetRoles), never from this
-- database -- docs/04 is explicit that the API authorises on claims rather
-- than a database lookup that could drift. Seeding a person here does not
-- grant them anything in Finance. Run scripts/bootstrap-app-roles.ps1 for
-- that half, and note that the two Finance roles must go to two different
-- people or AUTHORISE will refuse every posting.
--
-- SCOPE
-- -----
-- Dev only, same caveat as seed-dev-employee.sql: in a real environment
-- Employee rows come from an HR feed. Hand-inserting people is how a
-- directory drifts from the payroll it is meant to mirror. Step 10.
--
-- Idempotent, and keyed on EntraObjectId so re-running updates linkage
-- rather than duplicating people.
--
-- Invoke with -Variable @(
--   "RequesterOid=...","RequesterName=...","RequesterEmail=...",
--   "ManagerOid=...","ManagerName=...","ManagerEmail=...",
--   "HeadOid=...","HeadName=...","HeadEmail=...",
--   "OfficerOid=...","OfficerName=...","OfficerEmail=...",
--   "FinManagerOid=...","FinManagerName=...","FinManagerEmail=...")

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

DECLARE @DeptCode NVARCHAR(20) = N'ICT';

IF NOT EXISTS (SELECT 1 FROM Departments WHERE Code = @DeptCode)
BEGIN
    INSERT INTO Departments (Code, Name, IsActive)
    VALUES (@DeptCode, N'Information & Communications Technology', 1);
END

DECLARE @DepartmentId INT = (SELECT Id FROM Departments WHERE Code = @DeptCode);

DECLARE @Incoming TABLE (
    Slot          NVARCHAR(20)  NOT NULL,
    EntraObjectId NVARCHAR(200) NOT NULL,
    FullName      NVARCHAR(200) NOT NULL,
    Email         NVARCHAR(256) NOT NULL);

INSERT INTO @Incoming (Slot, EntraObjectId, FullName, Email)
VALUES
    (N'requester',  N'$(RequesterOid)',  N'$(RequesterName)',  N'$(RequesterEmail)'),
    (N'manager',    N'$(ManagerOid)',    N'$(ManagerName)',    N'$(ManagerEmail)'),
    (N'head',       N'$(HeadOid)',       N'$(HeadName)',       N'$(HeadEmail)'),
    (N'officer',    N'$(OfficerOid)',    N'$(OfficerName)',    N'$(OfficerEmail)'),
    (N'finmanager', N'$(FinManagerOid)', N'$(FinManagerName)', N'$(FinManagerEmail)');

-- ── People, no reporting lines yet ───────────────────────────────────────
-- Inserted before any LineManagerId is set, because the manager must exist
-- as a row before anyone can point at it. Doing this in one pass with the
-- links included would work only if the insert order happened to match the
-- hierarchy, which survives testing and fails on the first reorganisation.

UPDATE e
SET e.FullName = i.FullName,
    e.Email    = i.Email,
    e.IsActive = 1
FROM Employees e
JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId;

-- Staff numbers are allocated from whatever is free, not hardcoded per slot.
--
-- An earlier version assigned DEV-0001..DEV-0005 by position, on the
-- reasoning that seed-dev-employee.sql's existing DEV-0001 row would be the
-- requester and would therefore match and update rather than insert. That
-- holds only if the person already in dev happens to be the one you pass as
-- requester. Pass them as the line manager -- which is the natural thing to
-- do, since the person who has been signing in is usually senior to the test
-- requester -- and the requester slot tries to insert DEV-0001 on top of the
-- existing row: Msg 2601, UQ_Employee_StaffNumber.
DECLARE @NextSeq INT = ISNULL((
    SELECT MAX(TRY_CAST(SUBSTRING(StaffNumber, 5, 10) AS INT))
    FROM Employees
    WHERE StaffNumber LIKE N'DEV-%'), 0) + 1;

-- ROW_NUMBER over the distinct object ids, not over @Incoming: two slots can
-- legitimately name the same account, and inserting them twice would trip the
-- unique index on EntraObjectId instead.
INSERT INTO Employees (Id, EntraObjectId, StaffNumber, FullName, Email, DepartmentId, IsActive)
SELECT
    NEWID(),
    n.EntraObjectId,
    N'DEV-' + RIGHT(N'0000' + CAST(@NextSeq - 1 + ROW_NUMBER() OVER (ORDER BY n.EntraObjectId) AS NVARCHAR(10)), 4),
    n.FullName,
    n.Email,
    @DepartmentId,
    1
FROM (
    SELECT i.EntraObjectId, MIN(i.FullName) AS FullName, MIN(i.Email) AS Email
    FROM @Incoming i
    WHERE NOT EXISTS (SELECT 1 FROM Employees e WHERE e.EntraObjectId = i.EntraObjectId)
    GROUP BY i.EntraObjectId
) AS n;

DECLARE @RequesterId  UNIQUEIDENTIFIER = (SELECT e.Id FROM Employees e JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId WHERE i.Slot = N'requester');
DECLARE @ManagerId    UNIQUEIDENTIFIER = (SELECT e.Id FROM Employees e JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId WHERE i.Slot = N'manager');
DECLARE @HeadId       UNIQUEIDENTIFIER = (SELECT e.Id FROM Employees e JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId WHERE i.Slot = N'head');
DECLARE @OfficerId    UNIQUEIDENTIFIER = (SELECT e.Id FROM Employees e JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId WHERE i.Slot = N'officer');
DECLARE @FinManagerId UNIQUEIDENTIFIER = (SELECT e.Id FROM Employees e JOIN @Incoming i ON i.EntraObjectId = e.EntraObjectId WHERE i.Slot = N'finmanager');

-- ── Reporting lines ──────────────────────────────────────────────────────
-- The requester reports to the manager; the manager reports to the head. The
-- head reports to nobody, which is what stops LINE_MANAGER verification from
-- recursing forever and is why the workflow routes to DEPT_HEAD by state
-- rather than by walking the chain.
-- The <> guards stop a row becoming its own manager when two slots resolve to
-- the same account. Nothing in the schema forbids it, and the consequence is
-- not a crash: LineManagerOf would resolve the requester to themselves, and
-- the only thing standing between that and a self-approved claim would be the
-- ActorId != RequesterId guard. Defence in depth is the point, so the data
-- should not quietly rely on the guard to hold.
UPDATE Employees SET LineManagerId = @ManagerId WHERE Id = @RequesterId AND Id <> @ManagerId;
UPDATE Employees SET LineManagerId = @HeadId    WHERE Id = @ManagerId   AND Id <> @HeadId;

-- Finance sit outside the requester's reporting line deliberately. If the
-- Finance Officer reported to the same manager, a claim could be verified and
-- posted within one chain of command, which is the separation the two
-- signature blocks on DEL-AC-FRM-002 exist to create.
UPDATE Employees SET LineManagerId = @HeadId WHERE Id IN (@OfficerId, @FinManagerId) AND Id <> @HeadId;

UPDATE Departments SET DepartmentHeadId = @HeadId WHERE Id = @DepartmentId;

COMMIT TRANSACTION;

-- ── What now exists ──────────────────────────────────────────────────────
SELECT
    e.StaffNumber,
    e.FullName,
    e.Email,
    Manager = m.FullName,
    IsDepartmentHead = CASE WHEN d.DepartmentHeadId = e.Id THEN N'yes' ELSE N'' END,
    e.IsActive
FROM Employees e
JOIN Departments d ON d.Id = e.DepartmentId
LEFT JOIN Employees m ON m.Id = e.LineManagerId
WHERE d.Code = N'ICT'
ORDER BY e.StaffNumber;

PRINT '';
PRINT 'Reporting lines seeded. Finance roles are NOT granted by this script --';
PRINT 'run scripts/bootstrap-app-roles.ps1 and assign FinanceOfficer and';
PRINT 'FinanceManager to two DIFFERENT people, or AUTHORISE will refuse.';
