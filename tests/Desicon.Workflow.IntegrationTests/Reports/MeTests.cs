using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Reports;

/// <summary>
/// What the API says about the caller — because the browser now believes it.
/// </summary>
/// <remarks>
/// The Reports tab did not appear for the Cost Control desk. The browser was
/// reading `roles` off the MSAL account's ID token; the API reads it off the
/// access token it is sent. Two different tokens, and for that account the
/// first had no roles in it — so a tab was hidden from somebody the API would
/// have admitted immediately.
///
/// /api/v1/me exists so there is one answer instead of two. That makes this
/// endpoint load-bearing for every role-dependent thing the SPA will ever do,
/// which is precisely why it gets a test rather than an assumption.
/// </remarks>
public sealed class MeTests : IntegrationTestBase
{
    public MeTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Me_reports_the_roles_the_api_itself_reads()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ME-ROLES"));

        var me = await (await Fixture
            .CreateClient(org.CostControlOfficer, "CostControlOfficer")
            .GetAsync("/api/v1/me")).ShouldSucceedAsync();

        me.GetProperty("roles").EnumerateArray().Select(r => r.GetString())
            .Should().Contain("CostControlOfficer",
                "the browser decides what to draw from this, and it must match what the API enforces");

        me.GetProperty("hasEmployeeRecord").GetBoolean().Should().BeTrue();
        me.GetProperty("employee").GetString("fullName").Should().Be(org.CostControlOfficer.FullName);
    }

    [Fact]
    public async Task Me_reports_no_roles_for_somebody_who_holds_none()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ME-NONE"));

        var me = await (await Fixture.CreateClient(org.Requester)
            .GetAsync("/api/v1/me")).ShouldSucceedAsync();

        me.GetProperty("roles").EnumerateArray().Should().BeEmpty(
            "an ordinary requester holds no app role, and the Reports tab must not appear for them");

        me.GetProperty("hasEmployeeRecord").GetBoolean().Should().BeTrue();
    }
}
