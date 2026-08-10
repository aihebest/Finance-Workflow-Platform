using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Notifications;

/// <summary>
/// What gets enqueued, and — more to the point — what does not.
/// </summary>
/// <remarks>
/// Both definitions carry a blanket STATE_ENTERED → CurrentActor rule. On a
/// role-gated state there is no current actor by design, so those messages
/// were being created, failing at dispatch, and parking rows reading "No
/// recipients: could not resolve CurrentActor" while the correct person had
/// already been told by the state's own role rule.
///
/// Routine noise in a failure table is worse than no table: it is where the
/// next real failure hides. But the suppression has to be narrow, because the
/// same message is a genuine alarm on a state that IS resolved to a person —
/// EXP-2026-000004 was stranded exactly that way. Both halves are asserted
/// here, because a fix that silenced both would look identical in the outbox
/// and be considerably worse.
/// </remarks>
public sealed class OutboxEnqueueTests : IntegrationTestBase
{
    public OutboxEnqueueTests(WorkflowApiFixture fixture) : base(fixture) { }

    private async Task<List<(string Recipients, string Payload)>> MessagesForAsync(Guid requestId) =>
        await WithDbAsync(async db => await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.RequestId == requestId)
            .Select(m => new ValueTuple<string, string>(m.RecipientRolesJson, m.PayloadJson))
            .ToListAsync());

    [Fact]
    public async Task A_role_gated_state_enqueues_the_role_and_not_the_current_actor()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-ROLE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Cable", new DateOnly(2026, 2, 6), 50_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), id, "VERIFY"))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        // COST_CONTROL_VERIFY: every way out is gated on CostControlOfficer,
        // so no one person holds it and CurrentActorId is null by design.
        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("COST_CONTROL_VERIFY", StringComparison.Ordinal))
            .ToList();

        entering.Should().Contain(m => m.Recipients.Contains("CostControlOfficer", StringComparison.Ordinal),
            "the desk that has to act must still be told");

        entering.Should().NotContain(m => m.Recipients.Contains("CurrentActor", StringComparison.Ordinal),
            "a message with no possible recipient should never be created, let alone parked as a failure");
    }

    [Fact]
    public async Task A_person_resolved_state_still_enqueues_the_current_actor()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-PERSON"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Courier", new DateOnly(2026, 2, 6), 9_000m));

        // LINE_MANAGER resolves to one person, so CurrentActor is exactly who
        // should be addressed -- and if it ever resolves to nobody, the parked
        // message is the alarm this suppression must not swallow.
        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("LINE_MANAGER", StringComparison.Ordinal))
            .ToList();

        entering.Should().Contain(m => m.Recipients.Contains("CurrentActor", StringComparison.Ordinal),
            "a state resolved to a person must keep addressing that person, and must keep failing loudly if it cannot");
    }

    [Fact]
    public async Task A_terminal_state_enqueues_nothing_addressed_to_a_current_actor()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-TERM"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Router", new DateOnly(2026, 2, 6), 70_000m));

        await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.LineManager), id, "REJECT", comment: "Not approved."))
            .ShouldSucceedAsync();

        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("REJECTED", StringComparison.Ordinal))
            .ToList();

        entering.Should().Contain(m => m.Recipients.Contains("Requester", StringComparison.Ordinal),
            "the person who raised it must be told it was rejected");

        entering.Should().NotContain(m => m.Recipients.Contains("CurrentActor", StringComparison.Ordinal),
            "a terminal state has no outbound transition and therefore no actor to address");
    }
}
