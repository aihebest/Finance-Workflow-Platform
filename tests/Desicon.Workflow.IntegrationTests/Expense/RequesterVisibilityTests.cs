using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// That an approver can see who raised the request and out of which
/// department.
/// </summary>
/// <remarks>
/// Reported by Cost Control on 24 August 2026, in their own words: "Request did
/// not show which department the request is coming from."
///
/// The ids were never missing. RequesterId and DepartmentId have been in both
/// detail branches since the first release. They are GUIDs, so the response was
/// correct and the page was useless — the fact was on the wire and off the
/// screen, which is a distinction no test was making.
///
/// It bites hardest exactly where it was found. Cost Control's whole job at
/// COST_CONTROL_VERIFY is to check that spend is costed to the right centre,
/// and they were being asked to judge that without being told whose department
/// it came out of.
///
/// This is the second time on this project that an identifier stood in for a
/// fact and nobody noticed until a human tried to use it: BeneficiaryRef was
/// added in August after a claim was paid to the wrong person because no
/// approval screen showed a payee. Same shape, same remedy, and asserted this
/// time rather than assumed.
/// </remarks>
public sealed class RequesterVisibilityTests : IntegrationTestBase
{
    public RequesterVisibilityTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Cost_control_can_see_who_raised_a_request_and_from_where()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "REQ-VIS"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Office consumables", new DateOnly(2026, 3, 10), 20_200m));

        // Move it to the desk that raised the complaint.
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        var detail = await (await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        detail.GetString("currentState").Should().Be("COST_CONTROL_VERIFY");

        var requester = detail.GetProperty("requester");

        requester.GetString("name").Should().Be(org.Requester.FullName,
            "an approver needs a person, not a GUID");
        requester.GetString("staffNumber").Should().Be(org.Requester.StaffNumber);
        requester.GetString("department").Should().Be(org.Department.Name,
            "checking that spend is costed to the right centre is impossible without knowing " +
            "which department it came out of");
    }

    /// <summary>
    /// The same fact on a cash advance, which is the module Cost Control was
    /// looking at when they reported it.
    /// </summary>
    [Fact]
    public async Task The_same_is_true_of_a_cash_advance()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "REQ-VIS-ADV"));

        var id = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "September office consumables", 20_200m);

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        var detail = await (await Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        var requester = detail.GetProperty("requester");

        requester.GetString("name").Should().Be(org.Requester.FullName);
        requester.GetString("department").Should().Be(org.Department.Name);
    }
}
