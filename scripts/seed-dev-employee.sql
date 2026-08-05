-- Seeds a Department and an Employee for a real Entra user, so a person can
-- actually sign in to dev and see something.
--
-- WHY THIS IS NEEDED
-- ------------------
-- Authentication proves who you are; this platform also needs to know which
-- *employee* you are. ICurrentUserAccessor maps the token's `oid` claim to
-- Employee.EntraObjectId, and every worklist query is scoped by the resulting
-- Employee.Id. With no matching row the API answers, correctly and clearly:
--
--   No active Employee record is linked to Entra object id '...'
--
-- The integration tests never hit this because they create their own org
-- chart per test and Respawn removes it afterwards, so dev has never had a
-- single persistent person in it.
--
-- SCOPE
-- -----
-- Dev convenience only. In a real environment Employee rows come from the HR
-- feed or a directory sync -- hand-inserting people is how a directory drifts
-- from the payroll it is supposed to mirror. There is no such feed yet, and
-- that is a step 10 concern (see docs/07-Build-Plan.md).
--
-- Idempotent: safe to re-run, and updates the linkage if the person's Entra
-- object id changes.
--
-- Invoke with -Variable @("EntraObjectId=...","FullName=...","Email=...")

SET NOCOUNT ON;

DECLARE @DeptCode NVARCHAR(20) = N'ICT';

IF NOT EXISTS (SELECT 1 FROM Departments WHERE Code = @DeptCode)
BEGIN
    INSERT INTO Departments (Code, Name, IsActive)
    VALUES (@DeptCode, N'Information & Communications Technology', 1);

    PRINT 'Created department ' + @DeptCode + '.';
END

DECLARE @DepartmentId INT = (SELECT Id FROM Departments WHERE Code = @DeptCode);

IF EXISTS (SELECT 1 FROM Employees WHERE EntraObjectId = N'$(EntraObjectId)')
BEGIN
    UPDATE Employees
    SET FullName = N'$(FullName)',
        Email    = N'$(Email)',
        IsActive = 1
    WHERE EntraObjectId = N'$(EntraObjectId)';

    PRINT 'Updated existing employee.';
END
ELSE
BEGIN
    -- No LineManagerId: this person is the top of the tree in dev. Anything
    -- whose actor spec resolves to LineManagerOf will correctly find nobody
    -- rather than silently picking someone -- see EmployeeActorResolver,
    -- which treats a missing manager as a data problem for an admin, not
    -- something to paper over.
    INSERT INTO Employees
        (Id, EntraObjectId, StaffNumber, FullName, Email, DepartmentId, IsActive)
    VALUES
        (NEWID(), N'$(EntraObjectId)', N'DEV-0001', N'$(FullName)', N'$(Email)', @DepartmentId, 1);

    PRINT 'Created employee.';
END

SELECT e.Id, e.EntraObjectId, e.FullName, e.Email, d.Code AS Department, e.IsActive
FROM Employees e
JOIN Departments d ON d.Id = e.DepartmentId
WHERE e.EntraObjectId = N'$(EntraObjectId)';
