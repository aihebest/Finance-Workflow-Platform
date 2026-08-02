using System.Text;
using System.Text.Json;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Infrastructure.Persistence;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>A minimal org chart shared by the path/completeness/delegation
/// test classes: one department, one line-manager chain, one Finance pair.
/// Role membership (FinanceOfficer/FinanceManager) is not a DB concept here
/// -- it is granted per HttpClient via WorkflowApiFixture.CreateClient's
/// X-Test-Roles header, matching how TestAuthHandler/CurrentUserAccessor
/// read it.</summary>
public sealed record OrgChart(
    Department Department,
    Employee Requester,
    Employee LineManager,
    Employee DeptHead,
    Employee FinanceOfficer,
    Employee FinanceManager);

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
        var financeOfficer = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Finance Officer {suffix}");
        var financeManager = await TestData.CreateEmployeeAsync(db, department, $"{prefix} Finance Manager {suffix}");

        return new OrgChart(department, requester, lineManager, deptHead, financeOfficer, financeManager);
    }

    // ---------------------------------------------------------------
    // Raw HTTP wrappers -- one per route, no assertions.
    // ---------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object? body)
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

    public static Task<HttpResponseMessage> CaptureGlLinesExpenseAsync(
        HttpClient client, Guid id, string journalVoucherNumber, params object[] lines) =>
        PostAsync(client, $"/api/v1/expenses/{id}/gl-lines", new { JournalVoucherNumber = journalVoucherNumber, Lines = lines });

    public static Task<HttpResponseMessage> AuthorisePostingExpenseAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/expenses/{id}/authorise-posting", null);

    public static Task<HttpResponseMessage> AcknowledgeExpenseAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/expenses/{id}/acknowledge", null);

    public static Task<HttpResponseMessage> ConfirmRefundAsync(HttpClient client, Guid id, decimal refundReceivedAmountNgn) =>
        PostAsync(client, $"/api/v1/expenses/{id}/refund-received", new { RefundReceivedAmountNgn = refundReceivedAmountNgn });

    public static Task<HttpResponseMessage> ReleaseCashAsync(HttpClient client, Guid id, DateTimeOffset cashReleasedAt) =>
        PostAsync(client, $"/api/v1/advances/{id}/release", new { CashReleasedAt = cashReleasedAt });

    public static Task<HttpResponseMessage> CaptureGlLinesAdvanceAsync(
        HttpClient client, Guid id, string journalVoucherNumber, params object[] lines) =>
        PostAsync(client, $"/api/v1/advances/{id}/gl-lines", new { JournalVoucherNumber = journalVoucherNumber, Lines = lines });

    public static Task<HttpResponseMessage> AuthorisePostingAdvanceAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/advances/{id}/authorise-posting", null);

    public static Task<HttpResponseMessage> AcknowledgeAdvanceAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/advances/{id}/acknowledge", null);

    public static Task<HttpResponseMessage> RetireAdvanceAsync(HttpClient client, Guid id) =>
        PostAsync(client, $"/api/v1/advances/{id}/retire", null);

    public static object GlLine(string side, string accountNumber, decimal amountNgn, string? narration = null) =>
        new { side, accountNumber, narration, amountNgn };

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
        await (await ActionAsync(fixture.CreateClient(org.LineManager), id, "VERIFY")).ShouldSucceedAsync();
        await (await ActionAsync(fixture.CreateClient(org.DeptHead), id, "VERIFY")).ShouldSucceedAsync();
        await (await ActionAsync(
                fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = treasuryNumber }))
            .ShouldSucceedAsync();
    }

    /// <summary>Drives an expense claim from FINANCE_APPROVE through POSTING
    /// and AUTHORISATION to AWAITING_PAYMENT. Assumes NetPayableNgn >= 0 (the
    /// APPROVE action then targets POSTING, not REFUND_DUE).</summary>
    public static async Task DriveExpenseToAwaitingPaymentAsync(
        WorkflowApiFixture fixture, OrgChart org, Guid id, string journalVoucherNumber, decimal glAmountNgn)
    {
        await (await ActionAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();

        await (await CaptureGlLinesExpenseAsync(
                fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, journalVoucherNumber,
                GlLine("Debit", "1000-EXP", glAmountNgn), GlLine("Credit", "2000-CASH", glAmountNgn)))
            .ShouldSucceedAsync();

        await (await AuthorisePostingExpenseAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id))
            .ShouldSucceedAsync();
    }

    public static async Task<Guid> CreateAndSubmitCashAdvanceAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount)
    {
        var requesterClient = fixture.CreateClient(org.Requester);
        var created = await (await CreateCashAdvanceDraftAsync(requesterClient, purpose, amount)).ShouldSucceedAsync();
        var id = created.GetGuid("requestId");

        await (await SubmitAsync(requesterClient, id)).ShouldSucceedAsync();
        return id;
    }

    /// <summary>Drives a cash advance from DRAFT all the way to CASH_RELEASE
    /// (posted, authorised, awaiting release).</summary>
    public static async Task<Guid> DriveCashAdvanceToCashReleaseAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount,
        string treasuryNumber, string journalVoucherNumber)
    {
        var id = await CreateAndSubmitCashAdvanceAsync(fixture, org, purpose, amount);

        await (await ActionAsync(fixture.CreateClient(org.LineManager), id, "VERIFY")).ShouldSucceedAsync();
        await (await ActionAsync(fixture.CreateClient(org.DeptHead), id, "VERIFY")).ShouldSucceedAsync();
        await (await ActionAsync(
                fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = treasuryNumber }))
            .ShouldSucceedAsync();
        await (await ActionAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();
        await (await CaptureGlLinesAdvanceAsync(
                fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, journalVoucherNumber,
                GlLine("Debit", "1100-ADV", amount), GlLine("Credit", "2000-CASH", amount)))
            .ShouldSucceedAsync();
        await (await AuthorisePostingAdvanceAsync(fixture.CreateClient(org.FinanceManager, "FinanceManager"), id))
            .ShouldSucceedAsync();

        return id;
    }

    /// <summary>Drives a cash advance all the way to OUTSTANDING (released and
    /// acknowledged).</summary>
    public static async Task<Guid> DriveCashAdvanceToOutstandingAsync(
        WorkflowApiFixture fixture, OrgChart org, string purpose, decimal amount,
        string treasuryNumber, string journalVoucherNumber, DateTimeOffset releasedAt)
    {
        var id = await DriveCashAdvanceToCashReleaseAsync(fixture, org, purpose, amount, treasuryNumber, journalVoucherNumber);

        await (await ReleaseCashAsync(fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, releasedAt))
            .ShouldSucceedAsync();
        await (await AcknowledgeAdvanceAsync(fixture.CreateClient(org.Requester), id)).ShouldSucceedAsync();

        return id;
    }
}
