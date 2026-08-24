using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// What a person has already decided.
/// </summary>
/// <remarks>
/// Reported by Cost Control on 24 August 2026: "The System does not show the
/// history or trail of request that have been approved by cost control."
///
/// The gap was total. `/my/inbox` shows what is waiting on you and empties the
/// instant you act; `/my/requests` and `/my/advances` show what you raised.
/// Nothing showed what you had decided, so a desk verifying several advances a
/// day could not answer "did I already pass that one?" without asking somebody.
///
/// Strange company for that gap to keep: every action has been written to a
/// hash-chained, append-only AuditEvent since the first release. The record was
/// complete and had no reader — kept for an auditor who might come one day, and
/// withheld from the person who produced it.
/// </remarks>
public sealed class MyApprovalsTests : IntegrationTestBase
{
    public MyApprovalsTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Cost_control_can_see_what_it_has_already_verified()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "APPR-CC"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Office consumables", new DateOnly(2026, 3, 11), 20_200m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        // COST_CONTROL_VERIFY's guard wants receipts complete, at least one
        // attachment, and a Treasury number. Omitting the attachment refuses
        // the action with a 409 that says exactly that — which is the guard
        // doing its job, and was my omission rather than its fault.
        await WorkflowSteps.AttachReceiptAsync(Fixture, id, org.Requester.Id);

        var costControl = Fixture.CreateClient(org.CostControlOfficer, "CostControlOfficer");

        // Before acting: nothing of theirs.
        var before = await (await costControl.GetAsync("/api/v1/my/approvals")).ShouldSucceedAsync();
        before.GetProperty("totals").GetProperty("count").GetInt32().Should().Be(0);

        await (await WorkflowSteps.ActionAsync(costControl, id, "VERIFY",
            payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-APPR-1" }))
            .ShouldSucceedAsync();

        var requestNumber = await RequestNumberOfAsync(id);

        var after = await (await costControl.GetAsync("/api/v1/my/approvals")).ShouldSucceedAsync();

        after.GetProperty("totals").GetProperty("count").GetInt32().Should().Be(1,
            "the request left their inbox the moment they verified it, and this is the only place " +
            "it still appears to them");

        var row = after.GetProperty("approvals").EnumerateArray().Single();

        row.GetString("requestNumber").Should().Be(requestNumber);
        row.GetString("myAction").Should().Be("VERIFY");
        row.GetString("requester").Should().Be(org.Requester.FullName);
        row.GetProperty("isClosed").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// A requester's own submissions are not approvals.
    /// </summary>
    /// <remarks>
    /// SUBMIT and RESUBMIT are excluded deliberately. They are the requester's
    /// own act on their own request, they already appear under My Requests and
    /// My Advances, and including them would bury three decisions under thirty
    /// submissions — which for the desks that actually needed this screen is
    /// the difference between it being useful and being ignored.
    /// </remarks>
    [Fact]
    public async Task Raising_a_request_does_not_count_as_approving_one()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "APPR-REQ"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Courier", new DateOnly(2026, 3, 12), 5_000m));

        var mine = await (await Fixture.CreateClient(org.Requester)
            .GetAsync("/api/v1/my/approvals")).ShouldSucceedAsync();

        mine.GetProperty("totals").GetProperty("count").GetInt32().Should().Be(0,
            "submitting your own claim is not a decision on somebody else's");
    }

    /// <summary>
    /// One line per request, not one per action.
    /// </summary>
    /// <remarks>
    /// A Head of Department who returns a claim, receives it back and then
    /// verifies it has acted three times on one request. That is one line in
    /// their history showing the latest decision, or the screen becomes a log
    /// rather than an answer to "what have I dealt with".
    /// </remarks>
    [Fact]
    public async Task Acting_twice_on_one_request_shows_once_with_the_latest_decision()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "APPR-TWICE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Site fuel", new DateOnly(2026, 3, 13), 30_000m));

        var deptHead = Fixture.CreateClient(org.DeptHead);

        await (await WorkflowSteps.ActionAsync(deptHead, id, "RETURN", comment: "Attach the receipt."))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.Requester), id, "RESUBMIT"))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(deptHead, id, "VERIFY")).ShouldSucceedAsync();

        var history = await (await deptHead.GetAsync("/api/v1/my/approvals")).ShouldSucceedAsync();

        history.GetProperty("totals").GetProperty("count").GetInt32().Should().Be(1,
            "two actions on one request is one thing they dealt with, not two");

        history.GetProperty("approvals").EnumerateArray().Single()
            .GetString("myAction").Should().Be("VERIFY", "the latest decision is the useful one");
    }
}
