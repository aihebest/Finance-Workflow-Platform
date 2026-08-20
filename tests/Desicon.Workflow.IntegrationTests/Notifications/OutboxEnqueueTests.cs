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

        // DEPT_HEAD resolves to one person, so CurrentActor is exactly who
        // should be addressed -- and if it ever resolves to nobody, the parked
        // message is the alarm this suppression must not swallow.
        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("DEPT_HEAD", StringComparison.Ordinal))
            .ToList();

        entering.Should().Contain(m => m.Recipients.Contains("CurrentActor", StringComparison.Ordinal),
            "a state resolved to a person must keep addressing that person, and must keep failing loudly if it cannot");
    }

    /// <summary>
    /// One event, one message.
    /// </summary>
    /// <remarks>
    /// STATE_ENTERED is a catch-all. Where the definition also names a
    /// recipient for the state being entered, the catch-all produced a second
    /// message about the same event to the same person -- at RETURNED the
    /// requester received "action required" alongside "returned for
    /// correction".
    ///
    /// The specific one is always the more useful: it says what to do, where
    /// the generic one only says something is waiting.
    /// </remarks>
    [Fact]
    public async Task A_state_with_its_own_rule_does_not_also_get_the_catch_all()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-DUP"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Fuel", new DateOnly(2026, 2, 7), 20_000m));

        await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.DeptHead), id, "RETURN", comment: "Missing cost centre."))
            .ShouldSucceedAsync();

        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("RETURNED", StringComparison.Ordinal))
            .ToList();

        entering.Should().ContainSingle(
            "one event should produce one message; two mails for the same thing is how people learn to skim");

        entering[0].Recipients.Should().Contain("Requester");
    }

    /// <summary>
    /// The only branch where money comes back to Desicon.
    /// </summary>
    /// <remarks>
    /// A retirement showing the employee spent less than the advance lands in
    /// REFUND_DUE. Until 17 August 2026 that was the one state in either
    /// module with no notification at all: the employee was never told they
    /// owed money, and the Accounts Manager was never told to expect it. The
    /// claim simply sat.
    /// </remarks>
    [Fact]
    public async Task A_refund_due_tells_both_the_employee_who_owes_it_and_accounts()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-REFUND"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Site materials", new DateOnly(2026, 2, 7), 30_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-REF-1");

        // Took 50,000, spent 30,000: NetPayableNgn is negative and 20,000 is
        // owed back. Set on the entity rather than driven through a real
        // retirement, matching WorkflowCompletenessTests -- the notification
        // is what is under test, not the arithmetic that produced it.
        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == id);
            expense.AdvanceAmountNgn = 50_000m;
            await db.SaveChangesAsync();
        });

        var approved = await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();

        approved.GetString("toState").Should().Be("REFUND_DUE");

        var entering = (await MessagesForAsync(id))
            .Where(m => m.Payload.Contains("REFUND_DUE", StringComparison.Ordinal))
            .ToList();

        entering.Should().Contain(m => m.Recipients.Contains("Requester", StringComparison.Ordinal),
            "the person who owes the money is the one who has to pay it back");

        entering.Should().Contain(m => m.Recipients.Contains("FinanceManager", StringComparison.Ordinal),
            "Accounts has to know a refund is coming, and is the only role that can confirm it");
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
                Fixture.CreateClient(org.DeptHead), id, "REJECT", comment: "Not approved."))
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
