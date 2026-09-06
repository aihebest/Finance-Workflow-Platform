using System.Net;
using System.Net.Http.Json;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// That a person can actually finish retiring an advance, over HTTP, as
/// themselves.
/// </summary>
/// <remarks>
/// Found 6 September 2026. Retiring an advance could not be completed by
/// anybody, and never could.
///
/// <c>AdvanceRetirementEndpoints.RetireAsync</c> creates the linked expense
/// claim as a direct insert rather than through draft creation, and never sets
/// <c>ReceiptStatus</c>, so it takes the property default of <c>No</c>.
/// EXPENSE's SUBMIT guard requires <c>ReceiptStatus != 'No'</c>. The requester
/// pressed "Retire this advance", was taken to the claim the server had just
/// made for them, and could not move it — and there was no screen anywhere that
/// could change the field.
///
/// WHY NO TEST CAUGHT IT
/// ---------------------
/// One did, in a sense, and was read the wrong way round.
/// <c>CashAdvanceWorkflowTests</c> carried a note saying a retirement-linked
/// claim "is therefore stuck at DRAFT via the API alone", and got past it by
/// writing to the field through the <c>DbContext</c>. That was treated as a
/// test-harness inconvenience for eleven weeks. It was a description of a dead
/// end that a real person walks into.
///
/// Every path in the suite reached around the gap in the same way, which is
/// exactly why the suite stayed green while the feature did not work. So this
/// class does the whole thing over HTTP as the requester and touches the
/// database only to read.
///
/// WHY IT MATTERED MOST OF ALL OF THEM
/// -----------------------------------
/// The Director of Finance's objection to cash advances is that around 98
/// percent of them were never retired. Retirement is the single behaviour this
/// platform most needed to make easy. It was the one that did not work.
///
/// See docs/15-Go-Live-Checklist.md section 5h.
/// </remarks>
public sealed class RetirementCanBeCompletedTests : IntegrationTestBase
{
    public RetirementCanBeCompletedTests(WorkflowApiFixture fixture) : base(fixture) { }

    private static JsonContent Receipts(string status) =>
        JsonContent.Create(new { receiptStatus = status });

    private async Task<(OrgChart Org, Guid AdvanceId, Guid ClaimId)> RetiredAdvanceAsync(string tag)
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, tag));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Site running costs", 40_000m, "TN-RET-1", "BC-RET-1",
            Fixture.TimeProvider.GetUtcNow());

        var retired = await (await WorkflowSteps.RetireAdvanceAsync(
            Fixture.CreateClient(org.Requester), advanceId)).ShouldSucceedAsync();

        return (org, advanceId, retired.GetGuid("expenseRequestId"));
    }

    [Fact]
    public async Task A_requester_can_retire_an_advance_end_to_end_without_touching_the_database()
    {
        var (org, _, claimId) = await RetiredAdvanceAsync("RET-E2E");
        var requester = Fixture.CreateClient(org.Requester);

        // The dead end, asserted rather than described. This is the state the
        // server puts the claim in and the state the requester is shown.
        var deadEnd = await WorkflowSteps.SubmitAsync(requester, claimId);

        deadEnd.IsSuccessStatusCode.Should().BeFalse(
            "the claim is created with ReceiptStatus No and SUBMIT refuses No -- which is the " +
            "whole reason this endpoint exists");

        // The way out, over HTTP, as the person holding the claim.
        await (await requester.PatchAsync(
            $"/api/v1/requests/{claimId}/receipt-status", Receipts("Yes"))).ShouldSucceedAsync();

        // Expense version 7 wants the receipts themselves as well, which on a
        // retirement is the point: this claim is the account of what the
        // advance was spent on.
        await WorkflowSteps.AttachReceiptAsync(Fixture, claimId, org.Requester.Id);

        await (await WorkflowSteps.SubmitAsync(requester, claimId)).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == claimId).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DEPT_HEAD", "the retirement claim must be able to leave the requester");
        });
    }

    /// <summary>
    /// The change is recorded, not silent.
    /// </summary>
    /// <remarks>
    /// A claim that said No, then Yes, then was submitted is a different story
    /// from one that said Yes throughout, and the attachment timestamps sit
    /// beside this in the same trail.
    /// </remarks>
    [Fact]
    public async Task Answering_the_receipts_question_is_written_to_the_audit_trail()
    {
        var (org, _, claimId) = await RetiredAdvanceAsync("RET-AUDIT");

        await (await Fixture.CreateClient(org.Requester).PatchAsync(
            $"/api/v1/requests/{claimId}/receipt-status", Receipts("Incomplete"))).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEvents.AsNoTracking()
                .Where(e => e.RequestId == claimId && e.EventType == "RECEIPT_STATUS_SET")
                .OrderByDescending(e => e.AuditEventId)
                .FirstOrDefaultAsync();

            entry.Should().NotBeNull();
            entry!.ActorId.Should().Be(org.Requester.Id);
            entry.PayloadJson.Should().Contain("No").And.Contain("Incomplete",
                "the trail records what it was as well as what it became");

            // Nothing moved. A reader scanning for the route this claim took
            // should see it stayed put while a fact about it changed.
            entry.FromState.Should().Be(entry.ToState);
        });
    }

    [Fact]
    public async Task Nobody_else_can_answer_the_receipts_question_on_your_claim()
    {
        var (org, _, claimId) = await RetiredAdvanceAsync("RET-DENY");

        var refused = await Fixture.CreateClient(org.DeptHead)
            .PatchAsync($"/api/v1/requests/{claimId}/receipt-status", Receipts("Yes"));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an approver who thinks the answer is wrong has Return, not a way to answer for " +
            "somebody else");
    }

    [Fact]
    public async Task It_cannot_be_changed_once_the_claim_has_left_the_requester()
    {
        var (org, _, claimId) = await RetiredAdvanceAsync("RET-MOVED");
        var requester = Fixture.CreateClient(org.Requester);

        await (await requester.PatchAsync(
            $"/api/v1/requests/{claimId}/receipt-status", Receipts("Yes"))).ShouldSucceedAsync();
        await WorkflowSteps.AttachReceiptAsync(Fixture, claimId, org.Requester.Id);
        await (await WorkflowSteps.SubmitAsync(requester, claimId)).ShouldSucceedAsync();

        var refused = await requester.PatchAsync(
            $"/api/v1/requests/{claimId}/receipt-status", Receipts("No"));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "once an approver holds it, the answer they are reading must not change underneath " +
            "them");
    }
}
