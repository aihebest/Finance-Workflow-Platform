using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// The three worklist endpoints — the screens a person actually opens.
///
/// None of these had a test. The inbox in particular could not execute at
/// all: it included two collection navigations and then did
/// Concat(...).Distinct(), which EF cannot translate, and it failed the first
/// time a real user reached it. Every earlier call had failed further up —
/// at authentication, then at employee resolution — so the query itself was
/// never run.
///
/// A test asserting only that the endpoint returns 200 for a user with an
/// empty inbox would have passed. These assert the endpoint returns the
/// request that is actually waiting, which is the part that was broken.
/// </summary>
public sealed class WorklistTests : IntegrationTestBase
{
    public WorklistTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Inbox_returns_the_request_waiting_on_the_current_actor()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WL-A"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Waiting on the line manager", 5_000m);

        // Submitting parks it at DEPT_HEAD with the head of department as the
        // resolved actor, so it must appear in their inbox and nobody else's.
        var managerInbox = await (await Fixture.CreateClient(org.DeptHead)
            .GetAsync("/api/v1/my/inbox")).ShouldSucceedAsync();

        managerInbox.EnumerateArray()
            .Select(r => r.GetGuid("requestId"))
            .Should().Contain(requestId);

        var requesterInbox = await (await Fixture.CreateClient(org.Requester)
            .GetAsync("/api/v1/my/inbox")).ShouldSucceedAsync();

        requesterInbox.EnumerateArray()
            .Select(r => r.GetGuid("requestId"))
            .Should().NotContain(requestId, "the requester is not the one being waited on");
    }

    [Fact]
    public async Task Inbox_is_empty_for_someone_with_nothing_to_do()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WL-B"));

        var inbox = await (await Fixture.CreateClient(org.FinanceManager, "FinanceManager")
            .GetAsync("/api/v1/my/inbox")).ShouldSucceedAsync();

        inbox.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task My_requests_returns_what_I_raised_regardless_of_who_holds_it()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WL-C"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Mine, now with someone else", 2_000m);

        var mine = await (await Fixture.CreateClient(org.Requester)
            .GetAsync("/api/v1/my/requests")).ShouldSucceedAsync();

        var entry = mine.EnumerateArray().Single(r => r.GetGuid("requestId") == requestId);

        // The "where is it?" screen: the point is seeing the current holder
        // and state for something no longer in your hands.
        entry.GetString("currentState").Should().Be("DEPT_HEAD");
        entry.GetProperty("totalAmountNgn").GetDecimal().Should().Be(2_000m);
    }
}
