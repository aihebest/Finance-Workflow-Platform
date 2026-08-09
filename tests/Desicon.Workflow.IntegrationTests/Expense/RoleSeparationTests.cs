using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// That Cost Control and Treasury are actually separable.
/// </summary>
/// <remarks>
/// Workflow versions 1 and 2 gave both desks a single FinanceOfficer role, on
/// the understanding that they were the same people. They are not: they are
/// separate desks with separate mailboxes, and one role covering both meant
/// one holder could check the costing, post against their own check, and then
/// pay it.
///
/// Nothing failed. Every test passed, because every test drove both stages as
/// the same client -- so the suite could not have distinguished a system that
/// separated these duties from one that did not. That is the shape of this
/// whole project's findings: a control that is documented, provisioned, and
/// never once executed against the thing it is supposed to stop.
///
/// These tests execute it.
/// </remarks>
public sealed class RoleSeparationTests : IntegrationTestBase
{
    public RoleSeparationTests(WorkflowApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Cost_control_cannot_post_a_claim_it_verified()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SEP-POST"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Site fuel", new DateOnly(2026, 2, 2), 12_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-SEP-1");

        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE")).ShouldSucceedAsync();
        await (await WorkflowSteps.ApproveAsDirectorOfFinanceAsync(Fixture, org, id)).ShouldSucceedAsync();

        // The same person who verified the costing now tries to post it.
        var refused = await WorkflowSteps.MarkPostedExpenseAsync(
            Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer"), id, "BC-SEP-1");

        refused.IsSuccessStatusCode.Should().BeFalse(
            "AWAITING_POSTING is TreasuryOfficer's queue; Cost Control verifying and then posting its own " +
            "verification is exactly the collapse workflow version 3 exists to prevent");

        // And the claim has not moved.
        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id)
                .Select(r => r.CurrentState)
                .SingleAsync();

            state.Should().Be("AWAITING_POSTING");
        });
    }

    [Fact]
    public async Task Treasury_cannot_verify_the_costing_it_will_post()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SEP-VERIFY"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Courier", new DateOnly(2026, 2, 3), 4_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), id, "VERIFY"))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();
        await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

        var refused = await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer"), id, "VERIFY",
            payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-SEP-2" });

        refused.IsSuccessStatusCode.Should().BeFalse(
            "COST_CONTROL_VERIFY is CostControlOfficer's queue -- Treasury assigning its own Treasury " +
            "number and passing its own costing check is the same collapse from the other end");
    }

    /// <summary>
    /// The pinning mechanism, exercised for the first time against a real
    /// process change rather than a version number that never differed.
    /// </summary>
    /// <remarks>
    /// A claim raised under version 2 names FinanceOfficer at
    /// COST_CONTROL_VERIFY. Version 3 renamed that role. If definition
    /// resolution followed the latest published version rather than the one
    /// stamped on the request, this claim would become unactionable by
    /// anybody the moment version 3 shipped -- the failure
    /// docs/15 section 3 warns about, and the reason version 2 stays
    /// published.
    ///
    /// The version is set directly rather than by raising the request under an
    /// older definition, because creation always stamps the current version.
    /// That is the point: this simulates a request that was already in flight.
    /// </remarks>
    [Fact]
    public async Task A_request_pinned_to_version_2_still_resolves_the_version_2_role()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SEP-PIN"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Stationery", new DateOnly(2026, 2, 4), 6_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), id, "VERIFY"))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();
        await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

        await WithDbAsync(async db =>
        {
            var request = await db.Requests.SingleAsync(r => r.RequestId == id);
            request.DefinitionVersion.Should().Be(3, "a request raised today is stamped with the current version");
            request.DefinitionVersion = 2;
            await db.SaveChangesAsync();
        });

        // Version 3's role must NOT work on a version 2 request.
        var wrongVersion = await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer"), id, "VERIFY",
            payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-PIN-1" });

        wrongVersion.IsSuccessStatusCode.Should().BeFalse(
            "this request is pinned to version 2, which does not know the CostControlOfficer role");

        // Version 2's role must still work, or the request is stranded.
        await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.CostControlOfficer, "FinanceOfficer"), id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-PIN-1" }))
            .ShouldSucceedAsync();
    }
}
