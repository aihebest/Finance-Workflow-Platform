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

-- Upsert helper expressed inline rather than as a procedure: this file is run
-- through sqlcmd variable substitution, which happens before parsing, so a
-- procedure taking these as parameters would gain nothing and cost a
-- deployment artefact.
DECLARE @Upserted TABLE (Slot NVARCHAR(20), EmployeeId UNIQUEIDENTIFIER);

-- ── People, no reporting lines yet ───────────────────────────────────────
-- Inserted before any LineManagerId is set, because the manager must exist
-- as a row before anyone can point at it. Doing this in one pass with the
-- links included would work only if the insert order happened to match the
-- hierarchy, which is the kind of thing that survives testing and fails on
-- the first reorganisation.

MERGE Employees AS target
USING (VALUES
    (N'$(RequesterOid)',  N'DEV-0001', N'$(RequesterName)',  N'$(RequesterEmail)',  N'requester'),
    (N'$(ManagerOid)',    N'DEV-0002', N'$(ManagerName)',    N'$(ManagerEmail)',    N'manager'),
    (N'$(HeadOid)',       N'DEV-0003', N'$(HeadName)',       N'$(HeadEmail)',       N'head'),
    (N'$(OfficerOid)',    N'DEV-0004', N'$(OfficerName)',    N'$(OfficerEmail)',    N'officer'),
    (N'$(FinManagerOid)', N'DEV-0005', N'$(FinManagerName)', N'$(FinManagerEmail)', N'finmanager')
) AS source (EntraObjectId, StaffNumber, FullName, Email, Slot)
ON target.EntraObjectId = source.EntraObjectId
WHEN MATCHED THEN
    UPDATE SET
        FullName = source.FullName,
        Email    = source.Email,
        IsActive = 1
WHEN NOT MATCHED THEN
    INSERT (Id, EntraObjectId, StaffNumber, FullName, Email, DepartmentId, IsActive)
    VALUES (NEWID(), source.EntraObjectId, source.StaffNumber, source.FullName, source.Email, @DepartmentId, 1)
OUTPUT source.Slot, inserted.Id INTO @Upserted (Slot, EmployeeId);

DECLARE @RequesterId  UNIQUEIDENTIFIER = (SELECT EmployeeId FROM @Upserted WHERE Slot = N'requester');
DECLARE @ManagerId    UNIQUEIDENTIFIER = (SELECT EmployeeId FROM @Upserted WHERE Slot = N'manager');
DECLARE @HeadId       UNIQUEIDENTIFIER = (SELECT EmployeeId FROM @Upserted WHERE Slot = N'head');
DECLARE @OfficerId    UNIQUEIDENTIFIER = (SELECT EmployeeId FROM @Upserted WHERE Slot = N'officer');
DECLARE @FinManagerId UNIQUEIDENTIFIER = (SELECT EmployeeId FROM @Upserted WHERE Slot = N'finmanager');

-- ── Reporting lines ──────────────────────────────────────────────────────
-- The requester reports to the manager; the manager reports to the head. The
-- head reports to nobody, which is what stops LINE_MANAGER verification from
-- recursing forever and is why the workflow routes to DEPT_HEAD by state
-- rather than by walking the chain.
UPDATE Employees SET LineManagerId = @ManagerId WHERE Id = @RequesterId;
UPDATE Employees SET LineManagerId = @HeadId    WHERE Id = @ManagerId;

-- Finance sit outside the requester's reporting line deliberately. If the
-- Finance Officer reported to the same manager, a claim could be verified and
-- posted within one chain of command, which is the separation the two
-- signature blocks on DEL-AC-FRM-002 exist to create.
UPDATE Employees SET LineManagerId = @HeadId WHERE Id IN (@OfficerId, @FinManagerId);

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
