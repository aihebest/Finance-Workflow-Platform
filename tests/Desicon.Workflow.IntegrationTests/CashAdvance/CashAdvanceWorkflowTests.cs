using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

public sealed class CashAdvanceWorkflowTests : IntegrationTestBase
{
    public CashAdvanceWorkflowTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Releasing_cash_starts_the_retirement_clock_and_acknowledgement_moves_it_to_outstanding()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-RELEASE"));

        var id = await WorkflowSteps.DriveCashAdvanceToCashReleaseAsync(
            Fixture, org, "Field trip float", 5_000m, "TN-ADV-0001", "JV-ADV-0001");

        var releasedAt = Fixture.TimeProvider.GetUtcNow();
        var release = await (await WorkflowSteps.ReleaseCashAsync(
                Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), id, releasedAt))
            .ShouldSucceedAsync();
        release.GetString("toState").Should().Be("AWAITING_ACK");

        var afterRelease = await (await Fixture.CreateClient(org.Requester).GetAsync($"/api/v1/requests/{id}"))
            .ShouldSucceedAsync();
        afterRelease.GetProperty("cashReleasedAt").GetDateTimeOffset().Should().BeCloseTo(releasedAt, TimeSpan.FromSeconds(1));
        afterRelease.GetProperty("retirementDueDate").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        afterRelease.GetString("retirementStatus").Should().Be("NotDue");

        var acknowledge = await (await WorkflowSteps.AcknowledgeAdvanceAsync(Fixture.CreateClient(org.Requester), id))
            .ShouldSucceedAsync();
        acknowledge.GetString("toState").Should().Be("OUTSTANDING");

        var afterAck = await (await Fixture.CreateClient(org.Requester).GetAsync($"/api/v1/requests/{id}"))
            .ShouldSucceedAsync();
        afterAck.GetGuid("acknowledgedByUserId").Should().Be(org.Requester.Id);
        afterAck.GetProperty("acknowledgedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
    }

    // AdvanceRetirementEndpoints.RetireAsync inserts the linked ExpenseRequest
    // directly (it is a plain DB insert, not a guarded transition -- see that
    // file's class-level comment) and never sets ExpenseRequest.ReceiptStatus,
    // which defaults to ReceiptStatus.No. EXPENSE's SUBMIT guard requires
    // ReceiptStatus != 'No', so a retirement claim arrives already unable to
    // move, and this test pokes the field through the DbContext to get past it.
    //
    // 6 Sep 2026: half of the note that used to sit here was wrong, and the
    // wrong half mattered. It said "there is no HTTP endpoint that can change
    // ReceiptStatus on an existing DRAFT". There is -- PUT /api/v1/requests/
    // {id} routes to UpdateDraftAsync, which calls the same ApplyExpenseFields
    // that CreateDraftAsync does. What is missing is not the endpoint. It is
    // any caller: the SPA has no update-draft function and no edit-draft
    // route, so a requester who retires an advance lands on a claim they
    // cannot submit and cannot edit.
    //
    // This workaround therefore stands in for a dead end a real person hits,
    // not for a test-harness inconvenience. See docs/15 section 5h.
    [Fact]
    public async Task Full_retirement_via_a_linked_expense_claim_closes_the_advance()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-RETIRE"));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Regional office supplies", 6_000m, "TN-ADV-0002", "JV-ADV-0002",
            Fixture.TimeProvider.GetUtcNow());

        var retireResponse = await (await WorkflowSteps.RetireAdvanceAsync(Fixture.CreateClient(org.Requester), advanceId))
            .ShouldSucceedAsync();
        var expenseId = retireResponse.GetGuid("expenseRequestId");
        retireResponse.GetString("currentState").Should().Be("DRAFT");
        retireResponse.GetProperty("advanceAmountNgn").GetDecimal().Should().Be(6_000m);
        retireResponse.GetProperty("lineCount").GetInt32().Should().Be(1);

        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == expenseId);
            expense.ReceiptStatus = ReceiptStatus.Yes;
            await db.SaveChangesAsync();
        });

        // Expense version 7 also wants the receipt itself, which is the whole
        // point on a retirement: this claim is the account of what the advance
        // was spent on.
        await WorkflowSteps.AttachReceiptAsync(Fixture, expenseId, org.Requester.Id);

        await (await WorkflowSteps.SubmitAsync(Fixture.CreateClient(org.Requester), expenseId)).ShouldSucceedAsync();
        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, expenseId, "TN-EXP-RETIRE");

        var approve = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.FinanceManager, "FinanceManager"), expenseId, "APPROVE"))
            .ShouldSucceedAsync();
        // The claim is 6,000 against a 6,000 advance, so NetPayableNgn is
        // exactly zero: the employee spent what they took. Nobody is paid and
        // nobody owes anything, so the Director of Finance's gate does not
        // apply -- it authorises money leaving, and no money leaves. Straight
        // to Business Central.
        //
        // This is the ordinary case for a retirement, not an edge case, and it
        // is why the DMD branch is conditional on NetPayableNgn > 0 rather than
        // sitting on the path unconditionally.
        approve.GetString("toState").Should().Be("AWAITING_POSTING");

        // RetireLinkedAdvance fires on MARK_POSTED rather than on the journal
        // authorisation that no longer exists: the advance is retired at the
        // point Business Central records the claim against it. And because
        // there is nothing to pay, the claim closes here rather than joining a
        // payment queue it could never leave.
        var posted = await (await WorkflowSteps.MarkPostedExpenseAsync(
                Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), expenseId, "BC-EXP-RETIRE"))
            .ShouldSucceedAsync();
        posted.GetString("toState").Should().Be("CLOSED");

        var advanceAfter = await (await Fixture.CreateClient(org.Requester).GetAsync($"/api/v1/requests/{advanceId}"))
            .ShouldSucceedAsync();
        advanceAfter.GetString("currentState").Should().Be("CLOSED");
        advanceAfter.GetProperty("retiredAmountNgn").GetDecimal().Should().Be(6_000m);
        advanceAfter.GetProperty("retirementBalanceNgn").GetDecimal().Should().Be(0m);
        advanceAfter.GetProperty("closedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);

        var retirement = await (await Fixture.CreateClient(org.Requester).GetAsync($"/api/v1/advances/{advanceId}/retirement"))
            .ShouldSucceedAsync();
        var linkedClaims = retirement.GetProperty("linkedClaims");
        linkedClaims.GetArrayLength().Should().Be(1);
        linkedClaims[0].GetGuid("expenseRequestId").Should().Be(expenseId);
        linkedClaims[0].GetProperty("amountAppliedNgn").GetDecimal().Should().Be(6_000m);
    }
}
