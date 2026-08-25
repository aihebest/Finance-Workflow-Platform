using System.Net;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// That the person a request is waiting on can open it.
/// </summary>
/// <remarks>
/// Found on 25 August 2026 by the Director of Finance, doing exactly what the
/// platform asked him to do: he received "Payment approval required: cash
/// advance ADV-2026-000007", followed the link, signed in, and was told
///
///     You do not have access to this request.
///
/// `ReadAccessScope.CrossCuttingRoles` listed CostControlOfficer,
/// TreasuryOfficer, FinanceManager and ProcurementOfficer. It did not list
/// DirectorOfFinance — the one person who authorises every payment Desicon
/// makes. And `DMD_APPROVAL` is role-gated, so `CurrentActorId` is null there
/// by design, which removed the only other clause that could have admitted him.
///
/// Nothing failed. The notification sent, the link resolved, sign-in worked,
/// and the 403 was correct according to that list. The list was wrong. No test
/// could have caught it, because every test drove the DMD's approval directly
/// through the action endpoint and never once asked whether he could *read*
/// what he was approving.
///
/// The gate this entire platform is built around could not open the thing it
/// gates, and only a real approver following a real email could find that out.
/// </remarks>
public sealed class DirectorOfFinanceReadAccessTests : IntegrationTestBase
{
    public DirectorOfFinanceReadAccessTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task The_director_of_finance_can_open_a_request_waiting_for_authorisation()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DOF-READ"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Generator hire", new DateOnly(2026, 3, 16), 250_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-DOF-1");
        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE")).ShouldSucceedAsync();

        // Exactly what the email's link does: open the request as the DMD.
        var detail = await (await Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance")
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        detail.GetString("currentState").Should().Be("DMD_APPROVAL");

        detail.GetProperty("availableActions").EnumerateArray()
            .Select(a => a.GetString("action"))
            .Should().Contain("APPROVE",
                "he is being asked to authorise this payment, so the page must offer it");
    }

    /// <summary>
    /// The general rule, not just this role.
    /// </summary>
    /// <remarks>
    /// The class docstring on ReadAccessScope always said read access includes
    /// "a role-gated queue they hold a role for". It did not — both paths
    /// checked only CurrentActorId, which is null on precisely those states.
    ///
    /// Asserted with Treasury rather than the Director of Finance on purpose:
    /// Treasury is in CrossCuttingRoles, so this passes either way and proves
    /// nothing on its own. What it protects is the *rule*, so that the next
    /// role added to a definition does not depend on somebody also remembering
    /// to edit a list in a different file.
    /// </remarks>
    [Fact]
    public async Task A_role_gated_queue_is_readable_by_whoever_holds_that_role()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DOF-QUEUE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Site fuel", new DateOnly(2026, 3, 17), 40_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        var atCostControl = await (await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        atCostControl.GetString("currentState").Should().Be("COST_CONTROL_VERIFY");
    }

    /// <summary>
    /// Widening read access must not widen it to everybody.
    /// </summary>
    [Fact]
    public async Task Somebody_with_no_part_in_the_request_still_cannot_read_it()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DOF-DENY"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Stationery", new DateOnly(2026, 3, 18), 7_000m));

        // Somebody with no part in it: no role, not the requester, not their
        // line manager, not their department head, and this request is not in
        // any queue he holds.
        var outsider = await WithDbAsync(db =>
            TestData.CreateEmployeeAsync(db, org.Department, "Unrelated Colleague"));

        var refused = await Fixture.CreateClient(outsider).GetAsync($"/api/v1/requests/{id}");

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "reading is still scoped -- the fix widened it to the people a request is waiting on, " +
            "not to everyone who can sign in");
    }
}
