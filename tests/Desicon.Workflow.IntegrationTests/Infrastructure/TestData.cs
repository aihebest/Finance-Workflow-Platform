using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Persistence;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>Seeding helpers shared across the path/completeness/delegation
/// test classes -- pure data setup, no assertions.</summary>
public static class TestData
{
    public static async Task<Department> CreateDepartmentAsync(
        WorkflowDbContext db, string code, string name)
    {
        var department = new Department { Code = code, Name = name, IsActive = true };
        db.Departments.Add(department);
        await db.SaveChangesAsync();
        return department;
    }

    public static async Task SetDepartmentHeadAsync(WorkflowDbContext db, Department department, Employee head)
    {
        department.DepartmentHeadId = head.Id;
        await db.SaveChangesAsync();
    }

    public static async Task<Employee> CreateEmployeeAsync(
        WorkflowDbContext db, Department department, string fullName, Employee? lineManager = null)
    {
        var employee = new Employee
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            StaffNumber = Guid.NewGuid().ToString("N")[..10],
            FullName = fullName,
            Email = $"{Guid.NewGuid():N}@desicon.test",
            Grade = "G1",
            DepartmentId = department.Id,
            LineManagerId = lineManager?.Id,
            IsActive = true
        };

        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    /// <summary>
    /// AdvanceRetirementEndpoints.RetireAsync checks Employee.BankName /
    /// BankAccountNumber (not Beneficiary's) before it will create the linked
    /// expense claim -- see FindOrCreateEmployeeBeneficiaryAsync's
    /// MissingBankDetailsException guard. Only the retirement-via-linked-claim
    /// test needs this; every other test can use CreateEmployeeAsync directly.
    /// </summary>
    public static async Task SetBankDetailsAsync(WorkflowDbContext db, Employee employee)
    {
        // employee was loaded (and is tracked) by a different, already-
        // disposed WithDbAsync scope's DbContext, so it arrives here
        // detached. Without Attach first, mutating its properties and
        // calling SaveChangesAsync is a silent no-op -- no exception, no
        // rows written -- because this context's change tracker never knew
        // the entity existed.
        db.Employees.Attach(employee);
        employee.BankName = "Test Commercial Bank";
        employee.BankAccountNumber = "0123456789";
        await db.SaveChangesAsync();
    }

    public static async Task<Beneficiary> CreateEmployeeBeneficiaryAsync(WorkflowDbContext db, Employee employee)
    {
        var beneficiary = new Beneficiary
        {
            Type = BeneficiaryType.Employee,
            Name = employee.FullName,
            BankName = string.Empty,
            BankAccountNumber = string.Empty,
            EmployeeId = employee.Id
        };

        db.Beneficiaries.Add(beneficiary);
        await db.SaveChangesAsync();
        return beneficiary;
    }

    public static async Task<Delegation> CreateDelegationAsync(
        WorkflowDbContext db, Employee from, Employee to, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var delegation = new Delegation
        {
            FromEmployeeId = from.Id,
            ToEmployeeId = to.Id,
            StartsAt = startsAt,
            EndsAt = endsAt,
            IsActive = true
        };

        db.Delegations.Add(delegation);
        await db.SaveChangesAsync();
        return delegation;
    }

    /// <summary>
    /// LEAVE_REQUEST has no C# support for draft creation via the generic
    /// HTTP endpoint -- RequestEndpoints.CreateDraftAsync's ModuleKey switch
    /// only knows EXPENSE/CASH_ADVANCE (see the finding recorded in
    /// LeaveRequestWorkflowTests). Everything downstream of DRAFT is fully
    /// generic, so this seeds the one row the generic endpoints cannot
    /// create, and tests drive everything else through /submit and /actions.
    /// </summary>
    public static async Task<Request> CreateLeaveRequestDraftAsync(
        WorkflowDbContext db, Employee requester, DateTimeOffset now)
    {
        var request = new Request
        {
            ModuleKey = "LEAVE_REQUEST",
            FormCode = "DEL-HR-FRM-010",
            FormRevision = "01",

            // Seeding a row directly means taking on what CreateDraftAsync
            // would have done, and this is one of those things: the request is
            // evaluated against this version for the rest of its life. It must
            // match modules/leave-request.workflow.json, which is at version 1
            // -- EXPENSE and CASH_ADVANCE being at 2 says nothing about this
            // module.
            DefinitionVersion = 1,
            RequestNumber = $"LVE-{now:yyyy}-{Random.Shared.Next(100000, 999999)}",
            CurrentState = "DRAFT",
            StateEnteredAt = now,
            RequesterId = requester.Id,
            DepartmentId = requester.DepartmentId,
            RevisionNumber = 1
        };

        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>
    /// Attaches a receipt without touching blob storage.
    /// </summary>
    /// <remarks>
    /// COST_CONTROL_VERIFY refuses a claim with no evidence, so almost every
    /// path through the suite needs one. Inserting the row directly rather
    /// than uploading keeps the tests off Azure Storage -- the guard counts
    /// Attachments rows, and that is exactly what this provides.
    ///
    /// The blob it names does not exist, which is correct for these tests: they
    /// cover the workflow, and downloading is BlobAttachmentStore's business.
    /// </remarks>
    public static async Task<Attachment> AttachReceiptAsync(
        WorkflowDbContext db, Guid requestId, Guid uploadedBy, DateTimeOffset now, string fileName = "receipt.pdf")
    {
        var attachment = new Attachment
        {
            RequestId = requestId,
            FileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = 1024,
            BlobPath = $"{requestId:D}/{Guid.NewGuid():D}",
            Sha256 = new string('0', 64),
            UploadedByUserId = uploadedBy,
            UploadedAt = now
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment;
    }

    public static object ExpenseDraftPayload(Guid beneficiaryId, string receiptStatus, params object[] lines) =>
        new { beneficiaryId, receiptStatus, lines };

    public static object ExpenseLine(
        string description, DateOnly expenseDate, decimal amount,
        string? projectCode = null, string? costCentreCode = "CC-01",
        string currencyCode = "NGN", decimal fxRate = 1.0m) => new
        {
            description,
            expenseDate,
            expenseCategoryId = (int?)null,
            projectCode,
            costCentreCode,
            currencyCode,
            amount,
            fxRate,
            fxRateDate = expenseDate
        };

    public static object CashAdvanceDraftPayload(
        string purpose, decimal amount, string costCentreCode = "CC-01",
        string stationScope = "InStation", DateOnly? date = null) => new
        {
            purpose,
            allocationType = "CostCentre",
            projectCode = (string?)null,
            costCentreCode,
            stationScope,
            hasSupportingDocuments = true,
            lines = new[]
            {
                new
                {
                    description = purpose,
                    currencyCode = "NGN",
                    amount,
                    fxRate = 1.0m,
                    fxRateDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow)
                }
            }
        };
}
