using System.Net;
using System.Net.Http.Json;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// That a claim can name a payee who is not on the list, and that naming one
/// does not create a way to pay them.
/// </summary>
/// <remarks>
/// Asked for by Aihe on 10 September 2026, looking at the live expense form:
/// "Name of the Beneficiary is not typable". It was a dropdown of people
/// already in the system, and the person being paid is often a vendor, a casual
/// worker or a new starter who is in no list here — so the form was unfillable
/// for exactly the case it was most needed in.
///
/// The two halves of this are deliberately separate, and the separation is the
/// control. A requester may create a *name*. Only the desk that pays records
/// the *account*, against a specific claim, audited — and the payment guard
/// then refuses anyone who both set the account and approves paying into it.
/// Letting the requester supply both would collapse maker-checker into one
/// person, which is the arrangement it exists to prevent.
///
/// Until this change nothing in the browser could record a bank account at all.
/// <c>PUT /api/v1/requests/{id}/beneficiary/bank-details</c> shipped in the
/// first release, was tested, and had no caller — so a payee without an account
/// was a dead end with no way out, the same shape as the receipt-status gap in
/// §5h and the bank-details gap in §5j.
/// </remarks>
public sealed class TypedPayeeTests : IntegrationTestBase
{
    public TypedPayeeTests(WorkflowApiFixture fixture) : base(fixture) { }

    private static JsonContent Named(string? name) => JsonContent.Create(new { name });

    [Fact]
    public async Task A_requester_can_name_a_payee_who_is_not_on_the_list()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYEE-NEW"));

        var created = await (await Fixture.CreateClient(org.Requester)
            .PostAsync("/api/v1/beneficiaries", Named("Gbenga Motors Ltd")))
            .ShouldSucceedAsync();

        created.GetString("name").Should().Be("Gbenga Motors Ltd");
        created.GetString("type").Should().Be("Other");

        created.GetProperty("hasBankDetails").GetBoolean().Should().BeFalse(
            "naming somebody is not the same as being able to pay them");
    }

    /// <summary>
    /// An exact name match is refused, not quietly reused.
    /// </summary>
    /// <remarks>
    /// Two employees in this system share the display name "Best Aihebholoria",
    /// and on 9 August 2026 a claim was raised against the wrong one — §3e.
    /// Resolving a typed name to whichever row matched first would rebuild that
    /// fault and hide it inside a convenience; creating a second row with the
    /// same name would be worse. So the caller is sent to the list, where a
    /// staff number distinguishes people that a name cannot.
    /// </remarks>
    [Fact]
    public async Task A_name_that_already_exists_is_refused_and_the_caller_sent_to_the_list()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYEE-DUP"));
        var requester = Fixture.CreateClient(org.Requester);

        await (await requester.PostAsync("/api/v1/beneficiaries", Named("Gbenga Motors Ltd")))
            .ShouldSucceedAsync();

        var refused = await requester.PostAsync("/api/v1/beneficiaries", Named("Gbenga Motors Ltd"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await WithDbAsync(async db =>
        {
            var count = await db.Beneficiaries.CountAsync(b => b.Name == "Gbenga Motors Ltd");
            count.Should().Be(1, "the refusal must not have created a second one first");
        });
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYEE-BLNK"));

        var refused = await Fixture.CreateClient(org.Requester)
            .PostAsync("/api/v1/beneficiaries", Named("   "));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The claim page can see that the payee cannot be paid.
    /// </summary>
    /// <remarks>
    /// Without this the screen has no way to know it should offer the bank
    /// details panel, and the first anyone learns of the gap is Treasury being
    /// refused at the payment step with the Business Central posting already
    /// made.
    /// </remarks>
    [Fact]
    public async Task A_claim_raised_against_a_typed_payee_reports_that_it_has_no_account()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYEE-DTL"));
        var requester = Fixture.CreateClient(org.Requester);

        var payee = await (await requester.PostAsync(
            "/api/v1/beneficiaries", Named("Gbenga Motors Ltd"))).ShouldSucceedAsync();

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, payee.GetGuid("id"), "Yes",
            TestData.ExpenseLine("Vehicle repair", new DateOnly(2026, 9, 10), 250_000m));

        var detail = await (await requester.GetAsync($"/api/v1/requests/{id}"))
            .ShouldSucceedAsync();

        var beneficiary = detail.GetProperty("beneficiary");
        beneficiary.GetString("name").Should().Be("Gbenga Motors Ltd");
        beneficiary.GetProperty("hasBankDetails").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// And recording the account closes the gap.
    /// </summary>
    [Fact]
    public async Task Recording_the_account_makes_the_claim_payable()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYEE-BANK"));
        var requester = Fixture.CreateClient(org.Requester);

        var payee = await (await requester.PostAsync(
            "/api/v1/beneficiaries", Named("Gbenga Motors Ltd"))).ShouldSucceedAsync();

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, payee.GetGuid("id"), "Yes",
            TestData.ExpenseLine("Vehicle repair", new DateOnly(2026, 9, 10), 250_000m));

        await (await Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer")
            .PutAsync(
                $"/api/v1/requests/{id}/beneficiary/bank-details",
                JsonContent.Create(new { bankName = "Zenith Bank", bankAccountNumber = "0123456789" })))
            .ShouldSucceedAsync();

        var detail = await (await requester.GetAsync($"/api/v1/requests/{id}"))
            .ShouldSucceedAsync();

        detail.GetProperty("beneficiary").GetProperty("hasBankDetails").GetBoolean()
            .Should().BeTrue();

        await WithDbAsync(async db =>
        {
            var beneficiary = await db.Beneficiaries.AsNoTracking()
                .SingleAsync(b => b.Name == "Gbenga Motors Ltd");

            beneficiary.BankDetailsSetByUserId.Should().Be(org.TreasuryOfficer.Id,
                "who recorded the account is what the payment guard checks against -- the " +
                "same person cannot then approve paying into it");
        });
    }
}
