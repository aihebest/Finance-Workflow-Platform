using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// That neither module can be submitted without the evidence behind it.
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
/// what was provided, not what was asserted.
///
/// The expense claim followed one day later, for the same reason and against
/// the same field. Its SUBMIT guard checked `ReceiptStatus != 'No'` — the radio
/// on DEL-AC-FRM-002, the requester's account of the receipt — while the real
/// check sat two states downstream at Cost Control. So a claim with nothing
/// attached was approved by the Head of Department first and bounced after. And
/// the form had no upload control on it at all: the only way to attach anything
/// was the request page, after the claim had already been raised.
///
/// Both modules are now on version 7 and both count attachments at SUBMIT.
/// Cost Control keeps its own count as well, deliberately — a receipt can be
/// removed from a returned claim, and the desk that sends a payment for
/// authorisation should not rely on a check made upstream.
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
    /// An expense claim now needs its receipt at submission too.
    /// </summary>
    /// <remarks>
    /// Expense version 7, 6 September 2026. Until then SUBMIT checked
    /// <c>ReceiptStatus != 'No'</c> — the radio button on DEL-AC-FRM-002, which
    /// is the requester's account of the receipt and not the receipt. The real
    /// check, <c>AttachmentCount &gt; 0</c>, sat at COST_CONTROL_VERIFY, so a
    /// claim with nothing attached was approved by the Head of Department first
    /// and only then bounced, with two people's time already spent on it.
    ///
    /// The guard message on SUBMIT had read "Add at least one expense line and
    /// attach receipts before submitting" the entire time. It described a
    /// control that was not there.
    ///
    /// The reason recorded for keeping the check at Cost Control was that a
    /// claim is money already spent, so the receipt might follow it by a day.
    /// Put to Aihe on 5 September: not how Desicon works — the receipt exists
    /// before the claim is raised.
    /// </remarks>
    [Fact]
    public async Task An_expense_claim_cannot_be_submitted_with_no_receipt_attached()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-DOC-NONE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        var requester = Fixture.CreateClient(org.Requester);

        // ReceiptStatus "Yes" — the box says the receipt exists. Nothing is
        // attached, which is the distinction the change turns on.
        var created = await (await WorkflowSteps.CreateExpenseDraftAsync(
            requester, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Courier", new DateOnly(2026, 4, 1), 8_000m))).ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        var refused = await WorkflowSteps.SubmitAsync(requester, id);

        refused.IsSuccessStatusCode.Should().BeFalse(
            "the claim says Yes to receipts and has none; the guard counts attachments");

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DRAFT", "the refusal must leave it where the requester can fix it");
        });
    }

    /// <summary>
    /// And goes through once the receipt is actually there.
    /// </summary>
    [Fact]
    public async Task An_expense_claim_with_its_receipt_reaches_the_head_of_department()
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

            state.Should().Be("DEPT_HEAD");
        });
    }
}
