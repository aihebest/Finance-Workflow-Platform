using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.LeaveRequest;

/// <summary>
/// modules/leave-request.workflow.json is a module added with zero C#
/// changes and no migration: DRAFT..RETURNED..LINE_MANAGER..DEPT_HEAD..
/// CLOSED/REJECTED, driven entirely by generic engine machinery (guards
/// against base Request fields, the same LineManagerOf/DepartmentHeadOf
/// actor resolvers and IncrementRevisionNumber effect EXPENSE and
/// CASH_ADVANCE already use). The one wall the engine does *not* clear
/// generically is draft creation: RequestEndpoints.CreateDraftAsync
/// unconditionally calls definition.GetPolicyValue("PAYMENT_METHOD_THRESHOLD_NGN",
/// clock.UtcNow) for every module before it ever reaches the dto.ModuleKey
/// switch that only knows "EXPENSE" and "CASH_ADVANCE". LEAVE_REQUEST has no
/// such policy value at all (leave requests don't involve a payment method),
/// so that lookup throws InvalidOperationException -- mapped by
/// GlobalExceptionHandler to 404, not the switch's ArgumentException (400) --
/// before a Request row ever exists. That is the only place this module
/// needed help; TestData.CreateLeaveRequestDraftAsync seeds the DRAFT row
/// directly, and every transition downstream of it goes through the same
/// generic /submit and /actions routes as the other two modules, proving the
/// engine itself is generic.
/// </summary>
public sealed class LeaveRequestWorkflowTests : IntegrationTestBase
{
    public LeaveRequestWorkflowTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Creating_a_leave_request_via_the_generic_draft_endpoint_is_rejected()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-WALL"));

        var response = await Fixture.CreateClient(org.Requester).PostAsync(
            "/api/v1/requests",
            new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { ModuleKey = "LEAVE_REQUEST", Payload = new { } }),
                System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound,
            "CreateDraftAsync unconditionally looks up the PAYMENT_METHOD_THRESHOLD_NGN policy " +
            "value before ever reaching its ModuleKey switch, and LEAVE_REQUEST has no such policy " +
            "value -- this is the one place LEAVE_REQUEST needed help, everything downstream of DRAFT is generic");
    }

    [Fact]
    public async Task Two_stage_approval_closes_the_leave_request()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-HAPPY"));
        var draft = await WithDbAsync(db =>
            TestData.CreateLeaveRequestDraftAsync(db, org.Requester, Fixture.TimeProvider.GetUtcNow()));

        var requesterClient = Fixture.CreateClient(org.Requester);
        var lineManagerClient = Fixture.CreateClient(org.LineManager);
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);

        var submit = await (await WorkflowSteps.SubmitAsync(requesterClient, draft.RequestId)).ShouldSucceedAsync();
        submit.GetString("toState").Should().Be("LINE_MANAGER");

        var lmApprove = await (await WorkflowSteps.ActionAsync(lineManagerClient, draft.RequestId, "APPROVE"))
            .ShouldSucceedAsync();
        lmApprove.GetString("toState").Should().Be("DEPT_HEAD");

        var dhApprove = await (await WorkflowSteps.ActionAsync(deptHeadClient, draft.RequestId, "APPROVE"))
            .ShouldSucceedAsync();
        dhApprove.GetString("toState").Should().Be("CLOSED");

        var final = await (await requesterClient.GetAsync($"/api/v1/requests/{draft.RequestId}")).ShouldSucceedAsync();
        final.GetString("currentState").Should().Be("CLOSED");
        final.GetProperty("closedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Line_manager_return_and_resubmit_reaches_department_head_return()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-RETURN"));
        var draft = await WithDbAsync(db =>
            TestData.CreateLeaveRequestDraftAsync(db, org.Requester, Fixture.TimeProvider.GetUtcNow()));

        var requesterClient = Fixture.CreateClient(org.Requester);
        var lineManagerClient = Fixture.CreateClient(org.LineManager);
        var deptHeadClient = Fixture.CreateClient(org.DeptHead);

        await (await WorkflowSteps.SubmitAsync(requesterClient, draft.RequestId)).ShouldSucceedAsync();

        var lmReturn = await (await WorkflowSteps.ActionAsync(
                lineManagerClient, draft.RequestId, "RETURN", comment: "Please specify the leave dates."))
            .ShouldSucceedAsync();
        lmReturn.GetString("toState").Should().Be("RETURNED");

        var resubmit = await (await WorkflowSteps.ActionAsync(requesterClient, draft.RequestId, "RESUBMIT"))
            .ShouldSucceedAsync();
        resubmit.GetString("toState").Should().Be("LINE_MANAGER");

        await (await WorkflowSteps.ActionAsync(lineManagerClient, draft.RequestId, "APPROVE")).ShouldSucceedAsync();

        var dhReturn = await (await WorkflowSteps.ActionAsync(
                deptHeadClient, draft.RequestId, "RETURN", comment: "Overlaps a blackout period."))
            .ShouldSucceedAsync();
        dhReturn.GetString("toState").Should().Be("RETURNED");
    }

    [Fact]
    public async Task Line_manager_reject_is_terminal()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-LM-REJECT"));
        var draft = await WithDbAsync(db =>
            TestData.CreateLeaveRequestDraftAsync(db, org.Requester, Fixture.TimeProvider.GetUtcNow()));

        await (await WorkflowSteps.SubmitAsync(Fixture.CreateClient(org.Requester), draft.RequestId)).ShouldSucceedAsync();

        var reject = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.LineManager), draft.RequestId, "REJECT", comment: "Not eligible for leave yet."))
            .ShouldSucceedAsync();
        reject.GetString("toState").Should().Be("REJECTED");
    }

    [Fact]
    public async Task Department_head_reject_is_terminal()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-DH-REJECT"));
        var draft = await WithDbAsync(db =>
            TestData.CreateLeaveRequestDraftAsync(db, org.Requester, Fixture.TimeProvider.GetUtcNow()));

        await (await WorkflowSteps.SubmitAsync(Fixture.CreateClient(org.Requester), draft.RequestId)).ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), draft.RequestId, "APPROVE"))
            .ShouldSucceedAsync();

        var reject = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.DeptHead), draft.RequestId, "REJECT", comment: "Insufficient leave balance."))
            .ShouldSucceedAsync();
        reject.GetString("toState").Should().Be("REJECTED");
    }

    [Fact]
    public async Task Requester_cannot_approve_their_own_leave_request_even_as_their_own_line_manager()
    {
        // The DRAFT->LINE_MANAGER->DEPT_HEAD guard is "ActorId != RequesterId" on
        // both approval stages -- this test proves that guard actually fires
        // rather than being vacuously true because only the resolved actor
        // could ever call the action. Here the requester *is* the resolved
        // line manager (a self-managed edge case), and the generic guard
        // still blocks it with no module-specific code involved.
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "LVE-SELF"));
        var selfManaged = await WithDbAsync(db => TestData.CreateEmployeeAsync(db, org.Department, "Self-managed employee"));
        await WithDbAsync(async db =>
        {
            selfManaged.LineManagerId = selfManaged.Id;
            db.Employees.Update(selfManaged);
            await db.SaveChangesAsync();
        });

        var draft = await WithDbAsync(db =>
            TestData.CreateLeaveRequestDraftAsync(db, selfManaged, Fixture.TimeProvider.GetUtcNow()));

        var selfClient = Fixture.CreateClient(selfManaged);
        await (await WorkflowSteps.SubmitAsync(selfClient, draft.RequestId)).ShouldSucceedAsync();

        var approve = await WorkflowSteps.ActionAsync(selfClient, draft.RequestId, "APPROVE");
        approve.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict,
            "the resolved LineManagerOf actor and the requester are the same person here, so the request " +
            "clears actor resolution and fails on the ActorId != RequesterId guard instead (409, per " +
            "ProblemResults' TransitionOutcome.GuardFailed mapping)");
    }
}
