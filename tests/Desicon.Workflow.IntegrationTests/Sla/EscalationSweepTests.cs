using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Functions;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Workflow;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Sla;

/// <summary>
/// Step 6's second acceptance criterion: an SLA breach escalates and records
/// the person who did not act.
///
/// The distinction these tests defend is escalation transferring *authority*
/// rather than merely sending mail. If the Department Head cannot actually
/// action the escalated item, the SLA is advisory and the delay stays exactly
/// where it was — which is the specific failure digitising the paper form was
/// meant to fix.
/// </summary>
public sealed class EscalationSweepTests : IntegrationTestBase
{
    public EscalationSweepTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Breaching_the_line_manager_sla_transfers_authority_and_names_the_non_actor()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ESC-A"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Awaiting line manager", 3_000m);

        var before = await WithDbAsync(async db =>
            await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId));

        before.CurrentState.Should().Be("LINE_MANAGER");
        before.CurrentActorId.Should().Be(org.LineManager.Id, "the line manager is the one who must act");
        before.SlaDueAt.Should().NotBeNull();
        before.EscalationCount.Should().Be(0);

        // One second past the deadline. Nothing has happened in the meantime,
        // which is the whole scenario.
        await RunSweepAtAsync(before.SlaDueAt!.Value.AddSeconds(1));

        var after = await WithDbAsync(async db =>
            await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId));

        after.CurrentState.Should().Be("DEPT_HEAD", "sla.escalateTo names the state authority moves to");
        after.CurrentActorId.Should().Be(org.DeptHead.Id, "the department head can now act, not merely be told");
        after.EscalationCount.Should().Be(1);
        after.ReminderCount.Should().Be(0, "the new approver starts a fresh reminder cadence");
        after.SlaDueAt.Should().BeAfter(before.SlaDueAt!.Value, "the escalated state has its own deadline");

        // The audit trail must still answer "who was this waiting on".
        // CurrentActorId no longer says, because it now names the escalation
        // target.
        var escalation = await WithDbAsync(async db => await db.AuditEvents
            .AsNoTracking()
            .Where(e => e.RequestId == requestId && e.EventType == "ESCALATED")
            .SingleAsync());

        escalation.FromState.Should().Be("LINE_MANAGER");
        escalation.ToState.Should().Be("DEPT_HEAD");
        escalation.OnBehalfOfUserId.Should().Be(org.LineManager.Id, "the person who did not act is named");
        escalation.Reason.Should().Contain(org.LineManager.Id.ToString());
        escalation.ActorId.Should().NotBe(org.LineManager.Id, "escalation has no human actor");
    }

    /// <summary>
    /// The escalated approver can genuinely act. Asserting the state moved is
    /// not sufficient: authorisation is derived from the current state, so
    /// this is the assertion that actually proves authority transferred
    /// rather than that a column changed.
    /// </summary>
    [Fact]
    public async Task The_escalation_target_can_action_the_request_afterwards()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ESC-B"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Escalated then actioned", 2_500m);

        var slaDueAt = await WithDbAsync(async db =>
            (await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId)).SlaDueAt!.Value);

        await RunSweepAtAsync(slaDueAt.AddSeconds(1));

        var verify = await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.DeptHead), requestId, "VERIFY");

        verify.IsSuccessStatusCode.Should().BeTrue(
            "the department head holds the authority the line manager failed to exercise");
    }

    /// <summary>
    /// Escalating twice from the same state must not collapse into one audit
    /// entry, and escalating once must not write two. The idempotency key is
    /// keyed on EscalationCount rather than on the day, so a second sweep
    /// pass over an already-escalated request is a no-op while a genuine
    /// second breach is not.
    /// </summary>
    [Fact]
    public async Task Sweeping_twice_over_one_breach_escalates_once()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ESC-C"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Swept twice", 1_500m);

        var slaDueAt = await WithDbAsync(async db =>
            (await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId)).SlaDueAt!.Value);

        await RunSweepAtAsync(slaDueAt.AddSeconds(1));

        var afterFirst = await WithDbAsync(async db =>
            await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId));

        // Immediately again, before the new state's deadline.
        await RunSweepAtAsync(slaDueAt.AddMinutes(5));

        var afterSecond = await WithDbAsync(async db =>
            await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId));

        afterSecond.EscalationCount.Should().Be(afterFirst.EscalationCount);
        afterSecond.CurrentState.Should().Be(afterFirst.CurrentState);

        var escalations = await WithDbAsync(async db => await db.AuditEvents
            .Where(e => e.RequestId == requestId && e.EventType == "ESCALATED")
            .CountAsync());

        escalations.Should().Be(1);
    }

    /// <summary>
    /// Runs the real function against the real database, through the real SQL
    /// application lock and the real actor resolver.
    /// </summary>
    private async Task RunSweepAtAsync(DateTimeOffset now)
    {
        Fixture.TimeProvider.SetUtcNow(now);

        using var scope = Fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var sweep = new EscalationSweep(
            sp.GetRequiredService<WorkflowDbContext>(),
            sp.GetRequiredService<IWorkflowClock>(),
            sp.GetRequiredService<IWorkflowDefinitionProvider>(),
            sp.GetRequiredService<IActorResolver>(),
            sp.GetRequiredService<WorkflowEngine>(),
            NullLogger<EscalationSweep>.Instance);

        await sweep.RunAsync(new TimerInfo(), CancellationToken.None);
    }
}
