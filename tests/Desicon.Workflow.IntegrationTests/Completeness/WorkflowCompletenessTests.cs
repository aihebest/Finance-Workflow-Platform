using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using Desicon.Workflow.Infrastructure.Workflow;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Completeness;

/// <summary>
/// Standing coverage test: every state and every transition declared in the
/// EXPENSE and CASH_ADVANCE definitions must be exercised by at least one real
/// HTTP-driven request in this suite. Self-contained -- it does not rely on
/// coverage accumulated by other test classes (each test class gets its own
/// freshly-reset database, so cross-class accumulation is not possible
/// anyway), and instead drives one small request per branch, using layered
/// "DriveToX" helpers so a branch further down the state graph reuses the
/// steps that got it there instead of repeating them.
/// </summary>
public sealed class WorkflowCompletenessTests : IntegrationTestBase
{
    public WorkflowCompletenessTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Every_expense_state_and_transition_is_exercised_at_least_once()
    {
        var definition = await GetDefinitionAsync("EXPENSE");
        var expected = definition.Transitions.Select(t => (t.From, t.Action, t.To)).ToHashSet();
        var covered = new HashSet<(string From, string Action, string To)>();

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-COVER"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var requesterClient = Fixture.CreateClient(org.Requester);
        var lineManagerClient = Fixture.CreateClient(org.LineManager);
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);
        var financeOfficerClient = Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer");
        var financeManagerClient = Fixture.CreateClient(org.FinanceManager, "FinanceManager");

        async Task StepAsync(Func<Task<HttpResponseMessage>> call, string from, string action, string to)
        {
            var result = await (await call()).ShouldSucceedAsync();
            result.GetString("toState").Should().Be(to, "{0} --{1}--> {2} should have succeeded", from, action, to);
            covered.Add((from, action, to));
        }

        static Dictionary<string, object?> TreasuryNumber(string value) => new() { ["TreasuryNumber"] = value };

        async Task<Guid> CreateDraftAsync(string receiptStatus, decimal amount)
        {
            var created = await (await WorkflowSteps.CreateExpenseDraftAsync(
                    requesterClient, beneficiary.Id, receiptStatus,
                    TestData.ExpenseLine("Coverage line", DateOnly.FromDateTime(Fixture.TimeProvider.GetUtcNow().Date), amount)))
                .ShouldSucceedAsync();
            return created.GetGuid("requestId");
        }

        async Task<Guid> DriveToLineManagerAsync(string receiptStatus, decimal amount)
        {
            var id = await CreateDraftAsync(receiptStatus, amount);
            await StepAsync(() => WorkflowSteps.SubmitAsync(requesterClient, id), "DRAFT", "SUBMIT", "LINE_MANAGER");
            return id;
        }

        async Task<Guid> DriveToDeptHeadAsync(string receiptStatus, decimal amount)
        {
            var id = await DriveToLineManagerAsync(receiptStatus, amount);
            await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, id, "VERIFY"), "LINE_MANAGER", "VERIFY", "DEPT_HEAD");
            return id;
        }

        async Task<Guid> DriveToCostControlVerifyAsync(string receiptStatus, decimal amount)
        {
            var id = await DriveToDeptHeadAsync(receiptStatus, amount);
            await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, id, "VERIFY"), "DEPT_HEAD", "VERIFY", "COST_CONTROL_VERIFY");
            return id;
        }

        async Task<Guid> DriveToFinanceApproveAsync(decimal amount, string treasuryNumber)
        {
            var id = await DriveToCostControlVerifyAsync("Yes", amount);
            await StepAsync(
                () => WorkflowSteps.ActionAsync(financeOfficerClient, id, "VERIFY", payload: TreasuryNumber(treasuryNumber)),
                "COST_CONTROL_VERIFY", "VERIFY", "FINANCE_APPROVE");
            return id;
        }

        async Task<Guid> DriveToDmdApprovalAsync(decimal amount, string treasuryNumber)
        {
            var id = await DriveToFinanceApproveAsync(amount, treasuryNumber);
            await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, id, "APPROVE"), "FINANCE_APPROVE", "APPROVE", "DMD_APPROVAL");
            return id;
        }

        async Task<Guid> DriveToPostingAsync(decimal amount, string treasuryNumber)
        {
            var id = await DriveToDmdApprovalAsync(amount, treasuryNumber);
            await StepAsync(
                () => WorkflowSteps.ApproveAsDirectorOfFinanceAsync(Fixture, org, id),
                "DMD_APPROVAL", "APPROVE", "AWAITING_POSTING");
            return id;
        }

        async Task<Guid> DriveToAwaitingPaymentAsync(decimal amount, string treasuryNumber, string bcDocumentNumber)
        {
            var id = await DriveToPostingAsync(amount, treasuryNumber);
            await StepAsync(
                () => WorkflowSteps.MarkPostedExpenseAsync(financeOfficerClient, id, bcDocumentNumber),
                "AWAITING_POSTING", "MARK_POSTED", "AWAITING_PAYMENT");
            return id;
        }

        async Task<Guid> DriveToAwaitingAckAsync(decimal amount, string treasuryNumber, string journalVoucherNumber, string paymentReference)
        {
            var id = await DriveToAwaitingPaymentAsync(amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(
                () => WorkflowSteps.ExecutePaymentAsync(
                    financeOfficerClient, id, paymentReference, Fixture.TimeProvider.GetUtcNow()),
                "AWAITING_PAYMENT", "EXECUTE_PAYMENT", "AWAITING_ACK");
            return id;
        }

        // Happy path: DRAFT -> ... -> AWAITING_ACK -> CLOSED.
        var claim1 = await DriveToAwaitingAckAsync(1_000m, "TN-01", "JV-01", "PMT-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim1, "ACKNOWLEDGE"), "AWAITING_ACK", "ACKNOWLEDGE", "CLOSED");

        // LINE_MANAGER RETURN -> RESUBMIT -> DEPT_HEAD RETURN.
        var claim2 = await DriveToLineManagerAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, claim2, "RETURN", comment: "Attach receipts."), "LINE_MANAGER", "RETURN", "RETURNED");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim2, "RESUBMIT"), "RETURNED", "RESUBMIT", "LINE_MANAGER");
        await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, claim2, "VERIFY"), "LINE_MANAGER", "VERIFY", "DEPT_HEAD");
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, claim2, "RETURN", comment: "Not my cost centre."), "DEPT_HEAD", "RETURN", "RETURNED");

        // LINE_MANAGER REJECT.
        var claim3 = await DriveToLineManagerAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, claim3, "REJECT", comment: "Not a valid claim."), "LINE_MANAGER", "REJECT", "REJECTED");

        // DEPT_HEAD REJECT.
        var claim4 = await DriveToDeptHeadAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, claim4, "REJECT", comment: "Not a valid claim."), "DEPT_HEAD", "REJECT", "REJECTED");

        // FINANCE_VERIFY RETURN (Incomplete receipts).
        var claim5 = await DriveToCostControlVerifyAsync("Incomplete", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, claim5, "RETURN", comment: "Receipts incomplete."), "COST_CONTROL_VERIFY", "RETURN", "RETURNED");

        // FINANCE_APPROVE RETURN.
        var claim6 = await DriveToFinanceApproveAsync(500m, "TN-06");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim6, "RETURN", comment: "Wrong cost centre."), "FINANCE_APPROVE", "RETURN", "RETURNED");

        // FINANCE_APPROVE REJECT.
        var claim7 = await DriveToFinanceApproveAsync(500m, "TN-07");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim7, "REJECT", comment: "Not approved."), "FINANCE_APPROVE", "REJECT", "REJECTED");

        // FINANCE_APPROVE -> REFUND_DUE -> CONFIRM_REFUND -> POSTING -> RETURN.
        var claim8 = await DriveToFinanceApproveAsync(1_000m, "TN-08");
        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == claim8);
            expense.AdvanceAmountNgn = 1_500m;
            await db.SaveChangesAsync();
        });
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim8, "APPROVE"), "FINANCE_APPROVE", "APPROVE", "REFUND_DUE");
        await StepAsync(() => WorkflowSteps.ConfirmRefundAsync(financeManagerClient, claim8, 500m), "REFUND_DUE", "CONFIRM_REFUND", "AWAITING_POSTING");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, claim8, "RETURN", comment: "Wrong cost centre."), "AWAITING_POSTING", "RETURN", "RETURNED");

        // FINANCE_APPROVE -> AWAITING_POSTING and AWAITING_POSTING -> CLOSED:
        // the zero-net-payable branch. A retirement where the employee spent
        // exactly the advance pays nobody, so it skips the Director of Finance
        // and closes at posting rather than entering a payment queue with
        // nothing in it to pay.
        var claim8c = await DriveToFinanceApproveAsync(1_000m, "TN-08C");
        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == claim8c);
            expense.AdvanceAmountNgn = 1_000m;
            await db.SaveChangesAsync();
        });
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim8c, "APPROVE"), "FINANCE_APPROVE", "APPROVE", "AWAITING_POSTING");
        await StepAsync(() => WorkflowSteps.MarkPostedExpenseAsync(financeOfficerClient, claim8c, "BC-08C"), "AWAITING_POSTING", "MARK_POSTED", "CLOSED");

        // REFUND_DUE RETURN -- the exit that did not exist until version 2. An
        // employee who never pays back an over-drawn advance previously left
        // the claim parked with no action available to anyone.
        var claim8b = await DriveToFinanceApproveAsync(1_000m, "TN-08B");
        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == claim8b);
            expense.AdvanceAmountNgn = 1_500m;
            await db.SaveChangesAsync();
        });
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim8b, "APPROVE"), "FINANCE_APPROVE", "APPROVE", "REFUND_DUE");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, claim8b, "RETURN", comment: "Refund never received."), "REFUND_DUE", "RETURN", "RETURNED");

        // DMD_APPROVAL RETURN and REJECT.
        var claim9 = await DriveToDmdApprovalAsync(500m, "TN-09");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), claim9, "RETURN", comment: "Query the cost centre."), "DMD_APPROVAL", "RETURN", "RETURNED");

        var claim9b = await DriveToDmdApprovalAsync(500m, "TN-09B");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), claim9b, "REJECT", comment: "Not payable."), "DMD_APPROVAL", "REJECT", "REJECTED");

        // AWAITING_PAYMENT RETURN.
        var claim10 = await DriveToAwaitingPaymentAsync(500m, "TN-10", "JV-10");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, claim10, "RETURN", comment: "Cannot pay this beneficiary."), "AWAITING_PAYMENT", "RETURN", "RETURNED");

        // AWAITING_ACK REJECT (beneficiary disputes receipt) -> AWAITING_PAYMENT.
        var claim11 = await DriveToAwaitingAckAsync(500m, "TN-11", "JV-11", "PMT-11");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim11, "REJECT", comment: "Never received this payment."), "AWAITING_ACK", "REJECT", "AWAITING_PAYMENT");

        covered.Should().BeEquivalentTo(expected, "every declared EXPENSE transition should be exercised by at least one test");

        var coveredStates = covered.SelectMany(t => new[] { t.From, t.To }).ToHashSet();
        var expectedStates = definition.States.Select(s => s.Key).ToHashSet();
        coveredStates.Should().BeEquivalentTo(expectedStates, "every declared EXPENSE state should be entered by at least one test");
    }

    [Fact]
    public async Task Every_cash_advance_state_and_transition_is_exercised_at_least_once()
    {
        var definition = await GetDefinitionAsync("CASH_ADVANCE");
        var expected = definition.Transitions.Select(t => (t.From, t.Action, t.To)).ToHashSet();
        var covered = new HashSet<(string From, string Action, string To)>();

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-COVER"));

        var requesterClient = Fixture.CreateClient(org.Requester);
        var lineManagerClient = Fixture.CreateClient(org.LineManager);
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);
        var financeOfficerClient = Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer");
        var financeManagerClient = Fixture.CreateClient(org.FinanceManager, "FinanceManager");

        async Task StepAsync(Func<Task<HttpResponseMessage>> call, string from, string action, string to)
        {
            var result = await (await call()).ShouldSucceedAsync();
            result.GetString("toState").Should().Be(to, "{0} --{1}--> {2} should have succeeded", from, action, to);
            covered.Add((from, action, to));
        }

        static Dictionary<string, object?> TreasuryNumber(string value) => new() { ["TreasuryNumber"] = value };

        async Task<Guid> CreateDraftAsync(string purpose, decimal amount)
        {
            var created = await (await WorkflowSteps.CreateCashAdvanceDraftAsync(requesterClient, purpose, amount)).ShouldSucceedAsync();
            return created.GetGuid("requestId");
        }

        async Task<Guid> DriveToLineManagerAsync(string purpose, decimal amount)
        {
            var id = await CreateDraftAsync(purpose, amount);
            await StepAsync(() => WorkflowSteps.SubmitAsync(requesterClient, id), "DRAFT", "SUBMIT", "LINE_MANAGER");
            return id;
        }

        async Task<Guid> DriveToDeptHeadAsync(string purpose, decimal amount)
        {
            var id = await DriveToLineManagerAsync(purpose, amount);
            await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, id, "VERIFY"), "LINE_MANAGER", "VERIFY", "DEPT_HEAD");
            return id;
        }

        async Task<Guid> DriveToCostControlVerifyAsync(string purpose, decimal amount)
        {
            var id = await DriveToDeptHeadAsync(purpose, amount);
            await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, id, "VERIFY"), "DEPT_HEAD", "VERIFY", "COST_CONTROL_VERIFY");
            return id;
        }

        async Task<Guid> DriveToFinanceApproveAsync(string purpose, decimal amount, string treasuryNumber)
        {
            var id = await DriveToCostControlVerifyAsync(purpose, amount);
            await StepAsync(
                () => WorkflowSteps.ActionAsync(financeOfficerClient, id, "VERIFY", payload: TreasuryNumber(treasuryNumber)),
                "COST_CONTROL_VERIFY", "VERIFY", "FINANCE_APPROVE");
            return id;
        }

        async Task<Guid> DriveToDmdApprovalAsync(string purpose, decimal amount, string treasuryNumber)
        {
            var id = await DriveToFinanceApproveAsync(purpose, amount, treasuryNumber);
            await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, id, "APPROVE"), "FINANCE_APPROVE", "APPROVE", "DMD_APPROVAL");
            return id;
        }

        async Task<Guid> DriveToPostingAsync(string purpose, decimal amount, string treasuryNumber)
        {
            var id = await DriveToDmdApprovalAsync(purpose, amount, treasuryNumber);
            await StepAsync(
                () => WorkflowSteps.ApproveAsDirectorOfFinanceAsync(Fixture, org, id),
                "DMD_APPROVAL", "APPROVE", "AWAITING_POSTING");
            return id;
        }

        async Task<Guid> DriveToCashReleaseAsync(string purpose, decimal amount, string treasuryNumber, string bcDocumentNumber)
        {
            var id = await DriveToPostingAsync(purpose, amount, treasuryNumber);
            await StepAsync(
                () => WorkflowSteps.MarkPostedAdvanceAsync(financeOfficerClient, id, bcDocumentNumber),
                "AWAITING_POSTING", "MARK_POSTED", "CASH_RELEASE");
            return id;
        }

        async Task<Guid> DriveToAwaitingAckAsync(string purpose, decimal amount, string treasuryNumber, string journalVoucherNumber)
        {
            var id = await DriveToCashReleaseAsync(purpose, amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(
                () => WorkflowSteps.ReleaseCashAsync(financeOfficerClient, id, Fixture.TimeProvider.GetUtcNow()),
                "CASH_RELEASE", "RELEASE_CASH", "AWAITING_ACK");
            return id;
        }

        async Task<Guid> DriveToOutstandingAsync(string purpose, decimal amount, string treasuryNumber, string journalVoucherNumber)
        {
            var id = await DriveToAwaitingAckAsync(purpose, amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, id, "ACKNOWLEDGE"), "AWAITING_ACK", "ACKNOWLEDGE", "OUTSTANDING");
            return id;
        }

        async Task FullyRetireAsync(Guid id)
        {
            await WithDbAsync(async db =>
            {
                var advance = await db.CashAdvanceRequests.FirstAsync(a => a.RequestId == id);
                advance.RetiredAmountNgn = advance.TotalAmountNgn;
                await db.SaveChangesAsync();
            });
        }

        // Happy path to OUTSTANDING, then partial retire (self-loop), then full retire -> CLOSED.
        var advA = await DriveToOutstandingAsync("Happy path advance", 5_000m, "TN-A-01", "JV-A-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advA, "RETIRE"), "OUTSTANDING", "RETIRE", "PARTIALLY_RETIRED");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advA, "RETIRE"), "PARTIALLY_RETIRED", "RETIRE", "PARTIALLY_RETIRED");
        await FullyRetireAsync(advA);
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advA, "RETIRE"), "PARTIALLY_RETIRED", "RETIRE", "CLOSED");

        // OUTSTANDING -> CLOSED directly (fully retired in one shot).
        var advB = await DriveToOutstandingAsync("Direct close advance", 2_000m, "TN-B-01", "JV-B-01");
        await FullyRetireAsync(advB);
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advB, "RETIRE"), "OUTSTANDING", "RETIRE", "CLOSED");

        // OUTSTANDING WRITE_OFF.
        var advC = await DriveToOutstandingAsync("Write-off advance", 1_000m, "TN-C-01", "JV-C-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advC, "WRITE_OFF", comment: "Recipient left the company."), "OUTSTANDING", "WRITE_OFF", "REJECTED");

        // PARTIALLY_RETIRED WRITE_OFF.
        var advD = await DriveToOutstandingAsync("Partial write-off advance", 1_000m, "TN-D-01", "JV-D-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advD, "RETIRE"), "OUTSTANDING", "RETIRE", "PARTIALLY_RETIRED");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advD, "WRITE_OFF", comment: "Remaining balance unrecoverable."), "PARTIALLY_RETIRED", "WRITE_OFF", "REJECTED");

        // LINE_MANAGER RETURN -> RESUBMIT.
        var advE = await DriveToLineManagerAsync("Resubmit advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, advE, "RETURN", comment: "Add a cost centre."), "LINE_MANAGER", "RETURN", "RETURNED");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advE, "RESUBMIT"), "RETURNED", "RESUBMIT", "LINE_MANAGER");

        // LINE_MANAGER REJECT.
        var advF = await DriveToLineManagerAsync("LM reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(lineManagerClient, advF, "REJECT", comment: "Not approved."), "LINE_MANAGER", "REJECT", "REJECTED");

        // DEPT_HEAD RETURN.
        var advG = await DriveToDeptHeadAsync("DH return advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advG, "RETURN", comment: "Wrong allocation."), "DEPT_HEAD", "RETURN", "RETURNED");

        // DEPT_HEAD REJECT.
        var advH = await DriveToDeptHeadAsync("DH reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advH, "REJECT", comment: "Not approved."), "DEPT_HEAD", "REJECT", "REJECTED");

        // FINANCE_VERIFY RETURN.
        var advI = await DriveToCostControlVerifyAsync("FV return advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, advI, "RETURN", comment: "Missing supporting documents."), "COST_CONTROL_VERIFY", "RETURN", "RETURNED");

        // FINANCE_VERIFY REJECT.
        var advJ = await DriveToCostControlVerifyAsync("FV reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, advJ, "REJECT", comment: "Not approved."), "COST_CONTROL_VERIFY", "REJECT", "REJECTED");

        // FINANCE_APPROVE RETURN.
        var advK = await DriveToFinanceApproveAsync("FA return advance", 1_000m, "TN-K-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advK, "RETURN", comment: "Wrong cost centre."), "FINANCE_APPROVE", "RETURN", "RETURNED");

        // FINANCE_APPROVE REJECT.
        var advL = await DriveToFinanceApproveAsync("FA reject advance", 1_000m, "TN-L-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advL, "REJECT", comment: "Not approved."), "FINANCE_APPROVE", "REJECT", "REJECTED");

        // AWAITING_POSTING RETURN.
        var advM = await DriveToPostingAsync("Posting return advance", 1_000m, "TN-M-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, advM, "RETURN", comment: "Wrong cost centre."), "AWAITING_POSTING", "RETURN", "RETURNED");

        // DMD_APPROVAL RETURN and REJECT.
        var advN = await DriveToDmdApprovalAsync("DMD return advance", 1_000m, "TN-N-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), advN, "RETURN", comment: "Query this."), "DMD_APPROVAL", "RETURN", "RETURNED");

        var advN2 = await DriveToDmdApprovalAsync("DMD reject advance", 1_000m, "TN-N-02");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), advN2, "REJECT", comment: "Not payable."), "DMD_APPROVAL", "REJECT", "REJECTED");

        // CASH_RELEASE RETURN.
        var advO = await DriveToCashReleaseAsync("Release return advance", 1_000m, "TN-O-01", "JV-O-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, advO, "RETURN", comment: "Cannot release cash right now."), "CASH_RELEASE", "RETURN", "RETURNED");

        // AWAITING_ACK RETURN.
        var advP = await DriveToAwaitingAckAsync("Ack return advance", 1_000m, "TN-P-01", "JV-P-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeOfficerClient, advP, "RETURN", comment: "Recipient unreachable."), "AWAITING_ACK", "RETURN", "RETURNED");

        covered.Should().BeEquivalentTo(expected, "every declared CASH_ADVANCE transition should be exercised by at least one test");

        var coveredStates = covered.SelectMany(t => new[] { t.From, t.To }).ToHashSet();
        var expectedStates = definition.States.Select(s => s.Key).ToHashSet();
        coveredStates.Should().BeEquivalentTo(expectedStates, "every declared CASH_ADVANCE state should be entered by at least one test");
    }

    private async Task<WorkflowDefinition> GetDefinitionAsync(string moduleKey)
    {
        using var scope = Fixture.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionProvider>();
        return await provider.GetAsync(moduleKey);
    }
}
