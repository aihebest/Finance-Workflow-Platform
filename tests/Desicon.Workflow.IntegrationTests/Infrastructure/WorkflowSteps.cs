using System.Text;
using System.Text.Json;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>A minimal org chart shared by the path/completeness/delegation
/// test classes: one department, one line-manager chain, and the Accounts
/// desks. Role membership is not a DB concept here -- it is granted per
/// HttpClient via WorkflowApiFixture.CreateClient's X-Test-Roles header,
/// matching how TestAuthHandler/CurrentUserAccessor read it.
///
/// CostControlOfficer and TreasuryOfficer are deliberately DIFFERENT people.
/// Workflow version 2 had a single FinanceOfficer covering both desks, so
/// every test drove verification and posting as one employee and the suite
/// could not have noticed that no separation existed. Two employees here is
/// what makes CannotPostWhatTheyThemselvesVerified a real assertion rather
/// than a restatement of the fixture.</summary>
public sealed record OrgChart(
    Department Department,
    Employee Requester,
    Employee LineManager,
    Employee DeptHead,
    Employee CostControlOfficer,
    Employee TreasuryOfficer,
    Employee FinanceManager,
    Employee DirectorOfFinance);

/// <summary>
/// Shared HTTP-driving helpers for the EXPENSE and CASH_ADVANCE workflows.
/// Low-level PostAsync/ActionAsync wrappers return the raw HttpResponseMessage
/// so callers can assert success or a specific failure status; the "drive to
/// state X" composites assume every guard along the way is satisfied and
/// assert success internally, since they exist purely to get a request to a
/// known checkpoint state economically for tests focused on what happens next.
/// </summary>
public static class WorkflowSteps
{
    public static async Task<OrgChart> CreateOrgChartAsync(WorkflowDbContext db, string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var department = await TestData.CreateDepartmentAsync(db, $"{prefix}-{suffix}", $"{prefix} Department");

        var deptHead = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Dept Head {suffix}");
        await TestData.SetDepartmentHeadAsync(db, department, deptHead);

        var lineManager = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Line Manager {suffix}");
        var requester = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Requester {suffix}", lineManager);
        var costControlOfficer = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Cost Control Officer {suffix}");

        // Separate from Cost Control on purpose -- see OrgChart's remarks.
        var treasuryOfficer = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Treasury Officer {suffix}");

        var financeManager = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Finance Manager {suffix}");

        // A distinct person from the Accounts Manager. The DMD gate is only a
        // control if it is a second pair of eyes -- one employee holding both
        // roles would satisfy every guard while providing no separation at all.
        var directorOfFinance = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Director of Finance {suffix}");

        return new OrgChart(
            department, requester, lineManager, deptHead,
            costControlOfficer, treasuryOfficer, financeManager, directorOfFinance);
    }

    // ---------------------------------------------------------------
    // Raw HTTP wrappers -- one per route, no assertions.
    // ---------------------------------------------------------------

    /// <summary>
    /// Public so a test can post a body this file has no helper for -- the
    /// alternative is each test rebuilding the same JSON serialisation, and
    /// then only some of them staying in step with the API's conventions.
    /// </summary>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object? body)
    {
        var json = JsonSerializer.Serialize(body ?? new { });
        return await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    public static Task<HttpResponseMessage> CreateExpenseDraftAsync(
        HttpClient client, Guid beneficiaryId, string receiptStatus, params object[] lines) =>
        PostAsync(client, "/api/v1/requests", new
        {
            ModuleKey = "EXPENSE",
            Payload = TestData.ExpenseDraftPayload(beneficiaryId, receiptStatus, lines)
        });

    public static Task<HttpResponseMessage> CreateCashAdvanceDraftAsync(
        HttpClient client, string purpose, decimal amount,
        string costCentreCode = "CC-01", string stationScope = "InStation") =>
        PostAsync(client, "/api/v1/requests", new
        {
            ModuleKey = "CASH_ADVANCE",
            Payload = TestData.CashAdvanceDraftPayload(purpose, amount, costCentreCode, stationScope)
        });

    public static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid id, string? comment = null) =>
        PostAsync(client, $"/api/v1/requests/{id}/submit", new { Comment = comment });

    public static Task<HttpResponseMessage> ActionAsync(
        HttpClient client, Guid id, string action, object? payload = null, string? comment = null) =>
        PostAsync(client, $"/api/v1/requests/{id}/actions", new { Action = action, Comment = comment, Payload = payload });

    /// <summary>
    /// MARK_POSTED: the Accounts Officer records that she has posted in
    /// Business Central. Replaces the gl-lines / authorise-posting pair, which
    /// modelled a journal this platform no longer keeps -- BC owns the ledger
    /// from definition version 2 onward.
    /// </summary>
    public static Task<HttpResponseMessage> MarkPostedExpenseAsync(
        HttpClient client, Guid id, string bcDocumentNumber) =>
        PostAsync(client, $"/api/v1/expenses/{id}/mark-posted", new { BcDocumentNumber = bcDocumentNumber });

    public static Task<HttpResponseMessage> AcknowledgeExpenseAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/expenses/{id}/acknowledge", null);

    /// <summary>
    /// EXECUTE_PAYMENT through its own endpoint rather than /actions.
    ///
    /// The generic endpoint moves the state but leaves PaymentReference and
    /// PaymentDate null, because captured fields reach the audit event's
    /// PayloadJson and nothing copies them onto the entity. Tests that drive
    /// through payment should use this, so what they exercise is what the
    /// browser does.
    /// </summary>
    public static Task<HttpResponseMessage> ExecutePaymentAsync(
        HttpClient client, Guid id, string paymentReference, DateTimeOffset? paymentDate = null) =>
        PostAsync(client, $"/api/v1/expenses/{id}/execute-payment",
            new { PaymentReference = paymentReference, PaymentDate = paymentDate });

    public static Task<HttpResponseMessage> ConfirmRefundAsync(HttpClient client, Guid id, decimal refundReceivedAmountNgn) =>
        PostAsync(client, $"/api/v1/expenses/{id}/refund-received", new { RefundReceivedAmountNgn = refundReceivedAmountNgn });

    public static Task<HttpResponseMessage> ReleaseCashAsync(HttpClient client, Guid id, DateTimeOffset cashReleasedAt) =>
        PostAsync(client, $"/api/v1/advances/{id}/release", new { CashReleasedAt = cashReleasedAt });

    public static Task<HttpResponseMessage> MarkPostedAdvanceAsync(
        HttpClient client, Guid id, string bcDocumentNumber) =>
        PostAsync(client, $"/api/v1/advances/{id}/mark-posted", new { BcDocumentNumber = bcDocumentNumber });

    /// <summary>
    /// The Director of Finance's approval. Nothing is paid without it, so
    /// every driver that reaches a payment or a cash release goes through here.
    /// </summary>
    public static Task<HttpResponseMessage> ApproveAsDirectorOfFinanceAsync(
        WorkflowApiFixture fixture, OrgChart org, Guid id) =>
        ActionAsync(fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), id, "APPROVE");

    public static Task<HttpResponseMessage> AcknowledgeAdvanceAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/advances/{id}/acknowledge", null);

    public static Task<HttpResponseMessage> RetireAdvanceAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/advances/{id}/retire", null);

    // ---------------------------------------------------------------
    // Composite drivers -- happy-path only, assert success internally.
    // ---------------------------------------------------------------

    public static async Task<Guid> CreateAndSubmitExpenseAsync(
        WorkflowApiFixture fixture, OrgChart org, Guid beneficiaryId, string receiptStatus, params object[] lines)
    {
        var requesterClient = fixture.CreateClient(org.Requester);
        var created = await (await CreateExpenseDraftAsync(requesterClient, beneficiaryId, receiptStatus, lines))
            .ShouldSucceedAsync();
        var id = created.GetGuid("requestId");

        await (await SubmitAsync(requesterClient, id)).ShouldSucceedAsync();
        return id;
    }

    public static async Task DriveExpenseToFinanceApproveAsync(
        WorkflowApiFixture fixture, OrgChart org, Guid id, string treasuryNumber)
    {
        await (await ActionAsync(fixture.CreateClient(org.DeptHead), id, "VERIFY")).ShouldSucceedAsync();

        // Evidence, because COST_CONTROL_VERIFY will not pass a claim without
        // it: the Accounts Officer has to be able to see what was purchased.
        // Seeded straight into the table rather than uploaded, so the suite
        // needs no blob storage -- the guard counts rows, which is what this
        // creates.
        await AttachReceiptAsync(fixture, id, org.Requester.Id);

        await (await ActionAsync(
                fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer"), id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = treasuryNumber }))
            .ShouldSucceedAsync();
    }

    /// <summary>
    /// Attaches a receipt to a request, bypassing upload.
    /// </summary>
    public static async Task AttachReceiptAsync(
        WorkflowApiFixture fixture, Guid requestId, Guid uploadedBy)
    {
        using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await TestData.AttachReceiptAsync(db, requestId, uploadedBy, fixture.TimeProvider.GetUtcNow());
    }

    /// <summary>
    /// Drives an expense claim from FINANCE_APPROVE through the Director of
    /// Finance and the Business Central posting to AWAITING_PAYMENT.
    /// </summary>
    /// <remarks>
    /// Assumes NetPayableNgn &gt; 0. That is now load-bearing rather than
    /// incidental: APPROVE branches three ways on the net payable, and only the
    /// positive branch reaches the DMD. A claim that balanced exactly or is in
    /// refund goes to posting directly and closes there, so driving one of
    /// those through here would fail at the first step with a guard message
    /// about a branch the caller never intended.
    ///
    /// The parameter is a BC document number, not a JV number. This platform
    /// no longer keeps a journal.
    /// </remarks>
    public static async Task DriveExpenseToAwaitingPaymentAsync(
        WorkflowApiFixture fixture, OrgChart org, Guid id, string bcDocumentNumber)
    {
        await (await ActionAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();

        await (await ApproveAsDirectorOfFinanceAsync(fixture, org, id)).ShouldSucceedAsync();

        await (await MarkPostedExpenseAsync(
                fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), id, bcDocumentNumber))
            .ShouldSucceedAsync();
    }

    public static async Task<Guid> CreateAndSubmitCashAdvanceAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount,
        string stationScope = "InStation")
    {
        var requesterClient = fixture.CreateClient(org.Requester);
        var created = await (await CreateCashAdvanceDraftAsync(
            requesterClient, purpose, amount, stationScope: stationScope)).ShouldSucceedAsync();
        var id = created.GetGuid("requestId");

        await (await SubmitAsync(requesterClient, id)).ShouldSucceedAsync();
        return id;
    }

    /// <summary>Drives a cash advance from DRAFT all the way to CASH_RELEASE
    /// -- approved by Accounts, authorised by the Director of Finance, and
    /// posted in Business Central.</summary>
    public static async Task<Guid> DriveCashAdvanceToCashReleaseAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount,
        string treasuryNumber, string bcDocumentNumber,
        string stationScope = "InStation")
    {
        var id = await CreateAndSubmitCashAdvanceAsync(fixture, org, purpose, amount, stationScope);

        await (await ActionAsync(fixture.CreateClient(org.DeptHead), id, "VERIFY")).ShouldSucceedAsync();
        await (await ActionAsync(
                fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer"), id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = treasuryNumber }))
            .ShouldSucceedAsync();
        await (await ActionAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();
        await (await ApproveAsDirectorOfFinanceAsync(fixture, org, id)).ShouldSucceedAsync();
        await (await MarkPostedAdvanceAsync(
                fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), id, bcDocumentNumber))
            .ShouldSucceedAsync();

        return id;
    }

    /// <summary>Drives a cash advance all the way to OUTSTANDING (released and
    /// acknowledged).</summary>
    public static async Task<Guid> DriveCashAdvanceToOutstandingAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount,
        string treasuryNumber, string bcDocumentNumber, DateTimeOffset releasedAt,
        string stationScope = "InStation")
    {
        var id = await DriveCashAdvanceToCashReleaseAsync(
            fixture, org, purpose, amount, treasuryNumber, bcDocumentNumber, stationScope);

        await (await ReleaseCashAsync(fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), id, releasedAt))
            .ShouldSucceedAsync();
        await (await AcknowledgeAdvanceAsync(fixture.CreateClient(org.Requester), id)).ShouldSucceedAsync();

        return id;
    }
}
