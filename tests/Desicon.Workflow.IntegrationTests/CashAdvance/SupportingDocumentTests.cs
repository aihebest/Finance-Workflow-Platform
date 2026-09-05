using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// That a cash advance cannot be submitted without something supporting it.
/// </summary>
/// <remarks>
/// Asked for by the Director of Finance on 5 September 2026. He authorises
/// every payment Desicon makes, and his account is that roughly 98 percent of
/// advances raised on paper were never retired — so he was being asked to
/// release money against a purpose line and nothing else.
///
/// An expense claim has always needed a receipt: it is money already spent and
/// the evidence exists. An advance is money *not yet* spent, so the evidence is
/// a quotation, a pro-forma or a written request — and until version 7 nothing
/// required one.
///
/// `HasSupportingDocuments` existed the whole time. It is the printed form's
/// tick box: a claim the requester makes about themselves, captured, stored,
/// and read by no guard ever. The platform recorded that somebody said there
/// was a document. It never asked for the document.
///
/// So the guard counts `AttachmentCount` from the Attachments table instead —
/// the same way the expense claim's receipt check already works, and for the
/// same reason: what was provided, not what was asserted.
/// </remarks>
public sealed class SupportingDocumentTests : IntegrationTestBase
{
    public SupportingDocumentTests(WorkflowApiFixture fixture) : base(fixture) { }

    /// <summary>
    /// Ticking the box is not attaching a document.
    /// </summary>
    /// <remarks>
    /// The distinction this whole change turns on, and the reason the draft
    /// below deliberately has `hasSupportingDocuments` set: that field is on
    /// DEL-AC-FRM-003 and has been captured since the first release, but it is
    /// the requester's own assertion about themselves and no guard has ever
    /// read it. The platform recorded that somebody said there was a document.
    /// It never asked for the document.
    ///
    /// A guard reading that field instead would look identical in the
    /// definition, pass every test, and enforce nothing.
    /// </remarks>
    [Fact]
    public async Task An_advance_cannot_be_submitted_on_the_requesters_word_alone()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-DOC-NONE"));
        var requester = Fixture.CreateClient(org.Requester);

        // CashAdvanceDraftPayload sets hasSupportingDocuments = true.
        var created = await (await WorkflowSteps.CreateCashAdvanceDraftAsync(
            requester, "Site mobilisation", 250_000m)).ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        var refused = await WorkflowSteps.SubmitAsync(requester, id);

        refused.IsSuccessStatusCode.Should().BeFalse(
            "the box is ticked and nothing is attached; the guard counts attachments, not the " +
            "requester's word for it");

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DRAFT", "the refusal must leave it where the requester can fix it");
        });
    }

    [Fact]
    public async Task An_advance_with_a_document_attached_goes_through()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ADV-DOC-OK"));
        var requester = Fixture.CreateClient(org.Requester);

        var created = await (await WorkflowSteps.CreateCashAdvanceDraftAsync(
            requester, "Site mobilisation", 250_000m)).ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        // Attached while still a draft, which is what lets the form do this in
        // one click: create, attach, submit.
        await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

        await (await WorkflowSteps.SubmitAsync(requester, id)).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DEPT_HEAD");
        });
    }

    /// <summary>
    /// Expense claims are unchanged.
    /// </summary>
    /// <remarks>
    /// EXPENSE stays at version 6. Its receipt requirement sits at
    /// COST_CONTROL_VERIFY, not at SUBMIT, because a claim is raised against
    /// money already spent and the receipt may follow the claim by a day. This
    /// asserts that version 7 did not quietly reach across into the other
    /// module — the two definitions are separate on purpose and it is easy to
    /// forget that a shared guard field is not a shared rule.
    /// </remarks>
    [Fact]
    public async Task An_expense_claim_can_still_be_submitted_before_the_receipt_arrives()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-DOC-OK"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Courier", new DateOnly(2026, 4, 1), 8_000m));

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DEPT_HEAD",
                "the receipt is required at Cost Control, not at submission");
        });
    }
}
