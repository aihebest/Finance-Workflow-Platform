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
    /// A request pinned to a version nobody publishes any more must fail
    /// loudly, not fall back to the current one.
    /// </summary>
    /// <remarks>
    /// This test used to assert the opposite half: that a version-2 request
    /// still resolved the old FinanceOfficer role while version 3 requests
    /// got the new ones. Version 2 has since been retired -- the database was
    /// reset for UAT, nothing was pinned to it, and its definition files were
    /// deleted along with the role.
    ///
    /// What remains is the more important guarantee. Falling back to the
    /// current definition would evaluate a request against a process it was
    /// never raised under, silently, and that is precisely the behaviour
    /// pinning exists to remove. So the failure must name the version asked
    /// for and the versions that exist, because the fix -- restore the file --
    /// is only obvious if the message says so.
    ///
    /// The generic mechanism is covered by DefinitionVersionPinningTests
    /// against temporary files. This one runs against the real published
    /// definitions, which is where a retired version actually bites.
    /// </remarks>
    [Fact]
    public async Task A_request_pinned_to_a_retired_version_fails_loudly()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SEP-PIN"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Stationery", new DateOnly(2026, 2, 4), 6_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();
        await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

        await WithDbAsync(async db =>
        {
            var request = await db.Requests.SingleAsync(r => r.RequestId == id);
            request.DefinitionVersion.Should().Be(4, "a request raised today is stamped with the current version");

            // As if it had been raised before version 2 was retired.
            request.DefinitionVersion = 2;
            await db.SaveChangesAsync();
        });

        var act = async () => await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer"), id, "VERIFY",
            payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-PIN-1" });

        // Either an exception naming the versions, or a non-success response --
        // what must NOT happen is a 200 produced by quietly using version 4.
        try
        {
            var response = await act();

            response.IsSuccessStatusCode.Should().BeFalse(
                "evaluating this request against version 4 would apply a process it was never raised under");
        }
        catch (InvalidOperationException ex)
        {
            ex.Message.Should().Contain("version 2");
            ex.Message.Should().Contain("4", "the message must say what IS published, or the fix is guesswork");
        }
    }
}
