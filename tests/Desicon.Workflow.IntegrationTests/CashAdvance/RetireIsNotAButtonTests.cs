using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// That nobody can retire an advance by asking to.
/// </summary>
/// <remarks>
/// 7 September 2026. ADV-2026-000008 — ₦360,000, the first advance ever walked
/// end to end by real people — reached OUTSTANDING and the request page drew a
/// button labelled RETIRE. The requester pressed it, saw nothing change,
/// pressed it again, and the trail now reads:
///
///     RETIRE · OUTSTANDING → PARTIALLY_RETIRED
///     RETIRE · PARTIALLY_RETIRED → PARTIALLY_RETIRED
///
/// Nothing was retired. No claim, no receipts, no figure. The advance simply
/// declared itself partly accounted for, twice, and the audit trail — the
/// hash-chained one, the whole point of this platform — now carries two
/// entries saying a retirement happened on a day when none did.
///
/// The button was legitimate as far as anything could tell.
/// GetAvailableActionsAsync reported RETIRE as available because its actor is
/// the Requester and its guard only asks that a balance remains. Both true.
/// What neither expressed is that RETIRE is a consequence rather than a
/// choice: an advance is retired *by* the expense claim that accounts for it,
/// and AdvanceRetirementHandler fires the transition as a cascade once that
/// claim carries a figure.
///
/// And the completeness test had been firing RETIRE straight at the actions
/// endpoint for months, so the transition read as covered — which is how a
/// route nobody should have had came to look tested.
/// </remarks>
public sealed class RetireIsNotAButtonTests : IntegrationTestBase
{
    public RetireIsNotAButtonTests(WorkflowApiFixture fixture) : base(fixture) { }

    private async Task<(OrgChart Org, Guid AdvanceId)> OutstandingAdvanceAsync(string tag)
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, tag));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var id = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Network subscription", 360_000m, "TN-RB-1", "BC-RB-1",
            Fixture.TimeProvider.GetUtcNow());

        return (org, id);
    }

    [Fact]
    public async Task A_requester_cannot_retire_an_advance_by_asking_to()
    {
        var (org, id) = await OutstandingAdvanceAsync("RB-DIR");

        var refused = await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.Requester), id, "RETIRE");

        refused.IsSuccessStatusCode.Should().BeFalse(
            "retirement is what an accounted-for claim does to an advance, not a button");

        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.AsNoTracking()
                .SingleAsync(a => a.RequestId == id);

            advance.CurrentState.Should().Be("OUTSTANDING",
                "the refusal must leave the advance exactly where it was");
            advance.RetiredAmountNgn.Should().Be(0m);
        });
    }

    /// <summary>
    /// And the page never offers it.
    /// </summary>
    /// <remarks>
    /// The refusal above is the control. This is the reason nobody meets it:
    /// a button that always fails is a worse answer than no button, and the
    /// requester who pressed it twice did so because the screen invited them
    /// to and then said nothing.
    /// </remarks>
    [Fact]
    public async Task The_request_never_offers_retire_as_an_available_action()
    {
        var (org, id) = await OutstandingAdvanceAsync("RB-HID");

        var detail = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        detail.GetString("currentState").Should().Be("OUTSTANDING");

        detail.GetProperty("availableActions").EnumerateArray()
            .Select(a => a.GetString("action"))
            .Should().NotContain("RETIRE",
                "the advance is retired through the claim that accounts for it, and the page " +
                "should say so rather than offering a button that cannot work");
    }

    /// <summary>
    /// The cascade still works, which is the half that must not break.
    /// </summary>
    [Fact]
    public async Task The_engine_may_still_retire_an_advance_through_a_linked_claim()
    {
        var (org, id) = await OutstandingAdvanceAsync("RB-CAS");

        var draft = await (await WorkflowSteps.RetireAdvanceAsync(
            Fixture.CreateClient(org.Requester), id)).ShouldSucceedAsync();

        draft.GetString("currentState").Should().Be("DRAFT",
            "starting a retirement creates the claim that will account for the advance");
        draft.GetProperty("advanceAmountNgn").GetDecimal().Should().Be(360_000m,
            "the claim carries the outstanding balance as Cash Advance Taken");

        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.AsNoTracking()
                .SingleAsync(a => a.RequestId == id);

            advance.CurrentState.Should().Be("OUTSTANDING",
                "the advance moves when the claim is accounted for, not when it is raised");
        });
    }
}
