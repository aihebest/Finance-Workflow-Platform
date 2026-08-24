using System.Net;
using System.Net.Http.Json;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// Cost Control setting or correcting the coding on a request in its queue.
/// </summary>
/// <remarks>
/// Asked for by Cost Control on 24 August 2026 and confirmed by Finance: they
/// should be able to return a request for correction *and* to fix the cost
/// centre themselves. Both, not either.
///
/// The reason is that Cost Control holds the organisation's cost centres and
/// the requester generally does not. A code that arrives wrong or missing is
/// ordinary rather than careless, and returning every one of them makes the
/// desk that knows the answer ask the person who does not.
///
/// Most of what follows asserts the boundaries rather than the feature: who may
/// do it, when, and that the previous coding survives in the trail. The
/// capability is three lines; the constraints are the design.
/// </remarks>
public sealed class AllocationCorrectionTests : IntegrationTestBase
{
    public AllocationCorrectionTests(WorkflowApiFixture fixture) : base(fixture) { }

    private static HttpContent Correction(string? costCentre, string? project, string reason) =>
        JsonContent.Create(new { costCentreCode = costCentre, projectCode = project, reason });

    private async Task<Guid> AdvanceAtCostControlAsync(OrgChart org, string purpose)
    {
        var id = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(Fixture, org, purpose, 20_200m);

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        return id;
    }

    [Fact]
    public async Task Cost_control_can_correct_a_wrong_cost_centre_without_returning_the_request()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-FIX"));
        var id = await AdvanceAtCostControlAsync(org, "September office consumables");

        var response = await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .PatchAsync($"/api/v1/requests/{id}/allocation",
                Correction("CC-ADMIN-02", null, "Coded to Projects in error; this is HR & Admin."));

        await response.ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.AsNoTracking()
                .SingleAsync(a => a.RequestId == id);

            advance.CostCentreCode.Should().Be("CC-ADMIN-02");
            advance.ProjectCode.Should().BeNull("a request is coded one way or the other, never both");
            advance.CurrentState.Should().Be("COST_CONTROL_VERIFY",
                "correcting the coding is not a decision on the request; it stays where it was");
        });
    }

    /// <summary>
    /// What the coding was must survive what it became.
    /// </summary>
    /// <remarks>
    /// This is the figure that decides which budget carries the spend. A silent
    /// correction is indistinguishable from the original entry a week later,
    /// and if a department head ever asks why their centre was charged, the
    /// answer has to exist.
    /// </remarks>
    [Fact]
    public async Task The_previous_coding_is_recorded_rather_than_overwritten()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-TRAIL"));
        var id = await AdvanceAtCostControlAsync(org, "Site consumables");

        var originalCoding = await WithDbAsync(async db => await db.CashAdvanceRequests
            .AsNoTracking().Where(a => a.RequestId == id)
            .Select(a => a.CostCentreCode).SingleAsync());

        await (await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .PatchAsync($"/api/v1/requests/{id}/allocation",
                Correction("CC-PROJ-77", null, "Belongs to the Warri mobilisation."))).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var events = await db.AuditEvents.AsNoTracking()
                .Where(e => e.RequestId == id)
                .OrderBy(e => e.AuditEventId)
                .ToListAsync();

            var correction = events.Single(e => e.EventType == "ALLOCATION_SET");

            correction.ActorId.Should().Be(org.CostControlOfficer.Id);
            correction.Reason.Should().Be("Belongs to the Warri mobilisation.");
            correction.FromState.Should().Be(correction.ToState,
                "nothing moved, and the trail should not suggest a step that did not happen");

            correction.PayloadJson.Should().Contain(originalCoding,
                "the coding it had must still be readable after the coding it has");
            correction.PayloadJson.Should().Contain("CC-PROJ-77");

            // The chain still verifies through the inserted event, so a
            // correction cannot be used to break or fork the trail.
            correction.VerifyAgainst(events[^2].EventHash).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Nobody_but_cost_control_may_set_the_coding()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-WHO"));
        var id = await AdvanceAtCostControlAsync(org, "Stationery");

        foreach (var (person, role) in new[]
                 {
                     (org.Requester, (string?)null),
                     (org.DeptHead, null),
                     (org.FinanceManager, "FinanceManager"),
                     (org.TreasuryOfficer, "TreasuryOfficer")
                 })
        {
            var client = role is null ? Fixture.CreateClient(person) : Fixture.CreateClient(person, role);

            var refused = await client.PatchAsync($"/api/v1/requests/{id}/allocation",
                Correction("CC-SNEAK", null, "Not mine to set."));

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "{0} does not hold the organisation's cost centres", person.FullName);
        }
    }

    /// <summary>
    /// Cost Control holds the role permanently. They hold a given request only
    /// while it is in their queue.
    /// </summary>
    [Fact]
    public async Task The_coding_cannot_be_changed_once_the_request_has_moved_on()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-LATE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Generator hire", new DateOnly(2026, 3, 14), 90_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-ALLOC-1");

        var refused = await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .PatchAsync($"/api/v1/requests/{id}/allocation",
                Correction("CC-TOO-LATE", null, "Second thoughts."));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the Accounts Manager is looking at figures Cost Control already passed; changing the " +
            "coding underneath them is a Return, not an edit");
    }

    [Fact]
    public async Task A_project_code_and_a_cost_centre_are_alternatives_and_a_reason_is_required()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-BAD"));
        var id = await AdvanceAtCostControlAsync(org, "Courier");

        var costControl = Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer");

        var both = await costControl.PatchAsync($"/api/v1/requests/{id}/allocation",
            Correction("CC-01", "PRJ-01", "Both."));
        both.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a request is either project specific or it is not");

        var neither = await costControl.PatchAsync($"/api/v1/requests/{id}/allocation",
            Correction(null, null, "Neither."));
        neither.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var unexplained = await costControl.PatchAsync($"/api/v1/requests/{id}/allocation",
            Correction("CC-02", null, "   "));
        unexplained.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a change to the figure that decides which budget carries the spend has to say why");
    }

    /// <summary>
    /// An expense claim carries a code per line, because one claim can
    /// legitimately span two centres.
    /// </summary>
    [Fact]
    public async Task Correcting_an_expense_claim_recodes_every_line()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ALLOC-LINES"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Fuel", new DateOnly(2026, 3, 15), 10_000m),
            TestData.ExpenseLine("Tolls", new DateOnly(2026, 3, 15), 2_500m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        await (await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .PatchAsync($"/api/v1/requests/{id}/allocation",
                Correction("CC-FLEET-09", null, "Whole claim belongs to Fleet."))).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var lines = await db.ExpenseLines.AsNoTracking()
                .Where(l => l.RequestId == id)
                .ToListAsync();

            lines.Should().HaveCount(2);
            lines.Should().OnlyContain(l => l.CostCentreCode == "CC-FLEET-09");
            lines.Should().OnlyContain(l => l.ProjectCode == null);
        });
    }
}
