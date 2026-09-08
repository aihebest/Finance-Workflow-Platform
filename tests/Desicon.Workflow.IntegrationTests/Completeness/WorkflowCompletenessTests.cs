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
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);
        // Two desks, two people. Until workflow version 3 these were one
        // client, so this test walked every transition in both modules
        // without ever showing that Cost Control and Treasury are separable.
        var costControlClient = Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer");
        var treasuryClient = Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer");
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

            var id = created.GetGuid("requestId");

            // Version 7 will not submit a claim with no receipt attached.
            await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

            return id;
        }

        // One approval on the requesting side, not two. Version 4 removed the
        // line-manager step: Desicon's requester reports straight to a Head of
        // Department, and modelling an extra tier meant the same person
        // approving the same request twice.
        async Task<Guid> DriveToDeptHeadAsync(string receiptStatus, decimal amount)
        {
            var id = await CreateDraftAsync(receiptStatus, amount);
            await StepAsync(() => WorkflowSteps.SubmitAsync(requesterClient, id), "DRAFT", "SUBMIT", "DEPT_HEAD");
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

            // Cost Control keeps its own AttachmentCount check even though
            // version 7 now demands one at SUBMIT too, so this is a second
            // attachment. Left in deliberately: a receipt can be removed from
            // a returned claim, and the desk that sends a payment for
            // authorisation should not rely on a check made upstream.
            await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

            await StepAsync(
                () => WorkflowSteps.ActionAsync(costControlClient, id, "VERIFY", payload: TreasuryNumber(treasuryNumber)),
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
                () => WorkflowSteps.MarkPostedExpenseAsync(treasuryClient, id, bcDocumentNumber),
                "AWAITING_POSTING", "MARK_POSTED", "AWAITING_PAYMENT");
            return id;
        }

        async Task<Guid> DriveToAwaitingAckAsync(decimal amount, string treasuryNumber, string journalVoucherNumber, string paymentReference)
        {
            var id = await DriveToAwaitingPaymentAsync(amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(
                () => WorkflowSteps.ExecutePaymentAsync(
                    treasuryClient, id, paymentReference, Fixture.TimeProvider.GetUtcNow()),
                "AWAITING_PAYMENT", "EXECUTE_PAYMENT", "AWAITING_ACK");
            return id;
        }

        // Happy path: DRAFT -> ... -> AWAITING_ACK -> CLOSED.
        var claim1 = await DriveToAwaitingAckAsync(1_000m, "TN-01", "JV-01", "PMT-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim1, "ACKNOWLEDGE"), "AWAITING_ACK", "ACKNOWLEDGE", "CLOSED");

        // DEPT_HEAD RETURN -> RESUBMIT -> DEPT_HEAD RETURN. RESUBMIT returns to
        // the same approver who sent it back, which is the point: they asked
        // for the correction and they are the one who checks it was made.
        var claim2 = await DriveToDeptHeadAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, claim2, "RETURN", comment: "Attach receipts."), "DEPT_HEAD", "RETURN", "RETURNED");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim2, "RESUBMIT"), "RETURNED", "RESUBMIT", "DEPT_HEAD");
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, claim2, "RETURN", comment: "Not my cost centre."), "DEPT_HEAD", "RETURN", "RETURNED");

        // DEPT_HEAD REJECT.
        var claim3 = await DriveToDeptHeadAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, claim3, "REJECT", comment: "Not a valid claim."), "DEPT_HEAD", "REJECT", "REJECTED");

        // WITHDRAW, from both points it is offered (version 5). claim2 is
        // already sitting at RETURNED from the sequence above, which is exactly
        // the case: handed back, and the requester decides not to bother.
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim2, "WITHDRAW", comment: "Not worth reclaiming."), "RETURNED", "WITHDRAW", "WITHDRAWN");

        var claim4 = await DriveToDeptHeadAsync("Yes", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, claim4, "WITHDRAW", comment: "Raised in error."), "DEPT_HEAD", "WITHDRAW", "WITHDRAWN");

        // FINANCE_VERIFY RETURN (Incomplete receipts).
        var claim5 = await DriveToCostControlVerifyAsync("Incomplete", 500m);
        await StepAsync(() => WorkflowSteps.ActionAsync(costControlClient, claim5, "RETURN", comment: "Receipts incomplete."), "COST_CONTROL_VERIFY", "RETURN", "RETURNED");

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
        await StepAsync(() => WorkflowSteps.ActionAsync(treasuryClient, claim8, "RETURN", comment: "Wrong cost centre."), "AWAITING_POSTING", "RETURN", "RETURNED");

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
        await StepAsync(() => WorkflowSteps.MarkPostedExpenseAsync(treasuryClient, claim8c, "BC-08C"), "AWAITING_POSTING", "MARK_POSTED", "CLOSED");

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
        await StepAsync(() => WorkflowSteps.ActionAsync(treasuryClient, claim10, "RETURN", comment: "Cannot pay this beneficiary."), "AWAITING_PAYMENT", "RETURN", "RETURNED");

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

        // System-only transitions are excluded, not forgotten. RETIRE is fired
        // by AdvanceRetirementHandler when a linked expense claim accounts for
        // the money; no actor can request it, so a test that drives it through
        // the actions endpoint is not exercising the workflow -- it is
        // exercising a route that should not exist. This test covers what a
        // person can do. See the note further down, and
        // CashAdvanceWorkflowTests for the cascade itself.
        var expected = definition.Transitions
            .Where(t => !t.SystemOnly)
            .Select(t => (t.From, t.Action, t.To))
            .ToHashSet();
        var covered = new HashSet<(string From, string Action, string To)>();

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-COVER"));

        var requesterClient = Fixture.CreateClient(org.Requester);
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);
        // Two desks, two people. Until workflow version 3 these were one
        // client, so this test walked every transition in both modules
        // without ever showing that Cost Control and Treasury are separable.
        var costControlClient = Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer");
        var treasuryClient = Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer");
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
            var id = created.GetGuid("requestId");

            // Version 7 will not submit an advance with nothing attached.
            await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

            return id;
        }

        // Version 4: one approval on the requesting side. See the expense
        // walk above for why.
        async Task<Guid> DriveToDeptHeadAsync(string purpose, decimal amount)
        {
            var id = await CreateDraftAsync(purpose, amount);
            await StepAsync(() => WorkflowSteps.SubmitAsync(requesterClient, id), "DRAFT", "SUBMIT", "DEPT_HEAD");
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

            // No attachment step here, unlike the expense walk above. An
            // advance's evidence is now demanded at SUBMIT (version 7), so by
            // the time it reaches Cost Control it already has one; Cost
            // Control's own guard asks for the allocation code instead.
            await StepAsync(
                () => WorkflowSteps.ActionAsync(costControlClient, id, "VERIFY", payload: TreasuryNumber(treasuryNumber)),
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
                () => WorkflowSteps.MarkPostedAdvanceAsync(treasuryClient, id, bcDocumentNumber),
                "AWAITING_POSTING", "MARK_POSTED", "CASH_RELEASE");
            return id;
        }

        async Task<Guid> DriveToAwaitingAckAsync(string purpose, decimal amount, string treasuryNumber, string journalVoucherNumber)
        {
            var id = await DriveToCashReleaseAsync(purpose, amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(
                () => WorkflowSteps.ReleaseCashAsync(treasuryClient, id, Fixture.TimeProvider.GetUtcNow()),
                "CASH_RELEASE", "RELEASE_CASH", "AWAITING_ACK");
            return id;
        }

        async Task<Guid> DriveToOutstandingAsync(string purpose, decimal amount, string treasuryNumber, string journalVoucherNumber)
        {
            var id = await DriveToAwaitingAckAsync(purpose, amount, treasuryNumber, journalVoucherNumber);
            await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, id, "ACKNOWLEDGE"), "AWAITING_ACK", "ACKNOWLEDGE", "OUTSTANDING");
            return id;
        }

        // RETIRE is not driven here any more, and the reason is the whole point
        // of this file.
        //
        // Until 7 September 2026 this test fired RETIRE straight at the
        // actions endpoint as the requester, five times, and reported the
        // transition covered. That is not how an advance is retired. It is
        // retired *by* an expense claim -- the requester raises the claim, and
        // AdvanceRetirementHandler fires RETIRE as a cascade once the claim
        // carries a figure. Nobody presses it.
        //
        // Because this test pressed it, the transition looked exercised, and
        // the request page's button for it looked legitimate. On
        // ADV-2026-000008 somebody pressed that button and moved a real
        // ₦360,000 advance from OUTSTANDING to PARTIALLY_RETIRED with nothing
        // retired. The coverage was real; what it covered was a route that
        // should not have existed.
        //
        // RETIRE is now SystemOnly, excluded from `expected` above, and
        // exercised through the cascade it actually travels in
        // CashAdvanceWorkflowTests.Full_retirement_via_a_linked_expense_claim_
        // closes_the_advance. That it can no longer be requested by a person
        // is asserted in RetireIsNotAButtonTests.
        //
        // The states remain covered. PARTIALLY_RETIRED is entered below by
        // writing it, which is a harness shortcut and labelled as one -- not a
        // supported route dressed up as a test.

        // OUTSTANDING WRITE_OFF.
        var advC = await DriveToOutstandingAsync("Write-off advance", 1_000m, "TN-C-01", "JV-C-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advC, "WRITE_OFF", comment: "Recipient left the company."), "OUTSTANDING", "WRITE_OFF", "REJECTED");

        // PARTIALLY_RETIRED WRITE_OFF. The state is set directly: the only way
        // in is the RETIRE cascade, which belongs to the linked claim and is
        // covered where that claim is driven. Shortcut, said out loud.
        var advD = await DriveToOutstandingAsync("Partial write-off advance", 1_000m, "TN-D-01", "JV-D-01");
        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.FirstAsync(a => a.RequestId == advD);
            advance.CurrentState = "PARTIALLY_RETIRED";
            advance.RetiredAmountNgn = 400m;
            await db.SaveChangesAsync();
        });

        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advD, "WRITE_OFF", comment: "Remaining balance unrecoverable."), "PARTIALLY_RETIRED", "WRITE_OFF", "REJECTED");

        // DEPT_HEAD RETURN -> RESUBMIT.
        var advE = await DriveToDeptHeadAsync("Resubmit advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advE, "RETURN", comment: "Add a cost centre."), "DEPT_HEAD", "RETURN", "RETURNED");
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advE, "RESUBMIT"), "RETURNED", "RESUBMIT", "DEPT_HEAD");

        // DEPT_HEAD REJECT (second advance, so the first stays available).
        var advF = await DriveToDeptHeadAsync("HOD reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advF, "REJECT", comment: "Not approved."), "DEPT_HEAD", "REJECT", "REJECTED");

        // DEPT_HEAD RETURN.
        var advG = await DriveToDeptHeadAsync("DH return advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advG, "RETURN", comment: "Wrong allocation."), "DEPT_HEAD", "RETURN", "RETURNED");

        // DEPT_HEAD REJECT.
        var advH = await DriveToDeptHeadAsync("DH reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(deptHeadClient, advH, "REJECT", comment: "Not approved."), "DEPT_HEAD", "REJECT", "REJECTED");

        // WITHDRAW (version 5). advG is already at RETURNED from above.
        // This is the module and the step where the mistake that prompted
        // version 5 was actually made: ADV-2026-000001, raised in error, with
        // no way out that did not involve asking an approver to reject it.
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advG, "WITHDRAW", comment: "Abandoning this advance."), "RETURNED", "WITHDRAW", "WITHDRAWN");

        var advH2 = await DriveToDeptHeadAsync("Withdraw advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(requesterClient, advH2, "WITHDRAW", comment: "Raised in error."), "DEPT_HEAD", "WITHDRAW", "WITHDRAWN");

        // FINANCE_VERIFY RETURN.
        var advI = await DriveToCostControlVerifyAsync("FV return advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(costControlClient, advI, "RETURN", comment: "Missing supporting documents."), "COST_CONTROL_VERIFY", "RETURN", "RETURNED");

        // FINANCE_VERIFY REJECT.
        var advJ = await DriveToCostControlVerifyAsync("FV reject advance", 1_000m);
        await StepAsync(() => WorkflowSteps.ActionAsync(costControlClient, advJ, "REJECT", comment: "Not approved."), "COST_CONTROL_VERIFY", "REJECT", "REJECTED");

        // FINANCE_APPROVE RETURN.
        var advK = await DriveToFinanceApproveAsync("FA return advance", 1_000m, "TN-K-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advK, "RETURN", comment: "Wrong cost centre."), "FINANCE_APPROVE", "RETURN", "RETURNED");

        // FINANCE_APPROVE REJECT.
        var advL = await DriveToFinanceApproveAsync("FA reject advance", 1_000m, "TN-L-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(financeManagerClient, advL, "REJECT", comment: "Not approved."), "FINANCE_APPROVE", "REJECT", "REJECTED");

        // AWAITING_POSTING RETURN.
        var advM = await DriveToPostingAsync("Posting return advance", 1_000m, "TN-M-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(treasuryClient, advM, "RETURN", comment: "Wrong cost centre."), "AWAITING_POSTING", "RETURN", "RETURNED");

        // DMD_APPROVAL RETURN and REJECT.
        var advN = await DriveToDmdApprovalAsync("DMD return advance", 1_000m, "TN-N-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), advN, "RETURN", comment: "Query this."), "DMD_APPROVAL", "RETURN", "RETURNED");

        var advN2 = await DriveToDmdApprovalAsync("DMD reject advance", 1_000m, "TN-N-02");
        await StepAsync(() => WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance"), advN2, "REJECT", comment: "Not payable."), "DMD_APPROVAL", "REJECT", "REJECTED");

        // CASH_RELEASE RETURN.
        var advO = await DriveToCashReleaseAsync("Release return advance", 1_000m, "TN-O-01", "JV-O-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(treasuryClient, advO, "RETURN", comment: "Cannot release cash right now."), "CASH_RELEASE", "RETURN", "RETURNED");

        // AWAITING_ACK RETURN.
        var advP = await DriveToAwaitingAckAsync("Ack return advance", 1_000m, "TN-P-01", "JV-P-01");
        await StepAsync(() => WorkflowSteps.ActionAsync(treasuryClient, advP, "RETURN", comment: "Recipient unreachable."), "AWAITING_ACK", "RETURN", "RETURNED");

        covered.Should().BeEquivalentTo(expected, "every declared CASH_ADVANCE transition should be exercised by at least one test");

        // States a person can reach, which since version 8 is not all of them:
        // CLOSED on an advance is entered only by the RETIRE cascade, and RETIRE
        // is SystemOnly. Asserting against every declared state would fail for
        // the right reason and the wrong one at the same time -- it would be
        // telling us this test does not press a button it must never press.
        // CLOSED is covered where the cascade runs, in CashAdvanceWorkflowTests.
        var coveredStates = covered.SelectMany(t => new[] { t.From, t.To }).ToHashSet();
        var expectedStates = expected.SelectMany(t => new[] { t.From, t.To }).ToHashSet();
        coveredStates.Should().BeEquivalentTo(expectedStates,
            "every CASH_ADVANCE state reachable by an actor should be entered by at least one test");
    }

}
