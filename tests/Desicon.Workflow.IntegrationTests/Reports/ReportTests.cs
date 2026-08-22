using System.Text.Json;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Reports;

/// <summary>
/// The two questions Finance could not answer without asking somebody: how
/// much company cash is out there unaccounted for, and where the queue jams.
/// </summary>
/// <remarks>
/// The restriction is asserted first and deliberately. "Reports are limited to
/// Finance" is the kind of sentence that is easy to write into a document and
/// never once executed against somebody who should be refused — which is the
/// defect this project has found more often than any other.
/// </remarks>
public sealed class ReportTests : IntegrationTestBase
{
    public ReportTests(WorkflowApiFixture fixture) : base(fixture) { }

    private static decimal Ngn(JsonElement totals, string name) =>
        totals.GetProperty(name).GetDecimal();

    [Fact]
    public async Task Only_finance_may_open_a_report()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "RPT-AUTHZ"));

        foreach (var path in new[] { "/api/v1/reports/outstanding-advances", "/api/v1/reports/pipeline" })
        {
            // A requester, and a Head of Department who approves requests every
            // day. Approving is not the same as seeing company-wide spend.
            foreach (var client in new[]
                     {
                         Fixture.CreateClient(org.Requester),
                         Fixture.CreateClient(org.DeptHead)
                     })
            {
                var refused = await client.GetAsync(path);

                refused.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
                    "{0} spans departments; approving within one is not authority to see all of them", path);
            }

            // All four finance roles, because a check written against one of
            // them and assumed for the rest is how three of the four end up
            // locked out on the morning somebody needs the figure.
            foreach (var role in new[] { "CostControlOfficer", "TreasuryOfficer", "FinanceManager", "DirectorOfFinance" })
            {
                var allowed = await Fixture.CreateClient(org.FinanceManager, role).GetAsync(path);

                allowed.IsSuccessStatusCode.Should().BeTrue("{0} holds {1}", path, role);
            }
        }
    }

    /// <summary>
    /// DEL-AC-FRM-003 makes an unretired advance the recipient's personal
    /// liability. Until now nothing showed that liability anywhere but on the
    /// recipient's own screen.
    /// </summary>
    [Fact]
    public async Task Outstanding_advances_shows_what_is_owed_and_how_late_it_is()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "RPT-ADV"));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Site mobilisation", 250_000m, "TN-RPT-1", "BC-RPT-1",
            Fixture.TimeProvider.GetUtcNow());

        // Nine days past the deadline. Set directly rather than by advancing
        // the clock: the report is under test, not the retirement calendar,
        // which RetirementSweepTests already covers.
        //
        // Both dates move. CK_RetirementDue enforces
        // RetirementDueDate >= CashReleasedAt, so backdating the deadline
        // alone produces an advance that was due before it was paid out --
        // which the database refuses, correctly.
        await WithDbAsync(async db =>
        {
            var now = Fixture.TimeProvider.GetUtcNow();
            var advance = await db.CashAdvanceRequests.SingleAsync(a => a.RequestId == advanceId);

            advance.CashReleasedAt = now.AddDays(-12);
            advance.RetirementDueDate = now.AddDays(-9);
            await db.SaveChangesAsync();
        });

        var advanceNumber = await RequestNumberOfAsync(advanceId);

        var report = await (await Fixture.CreateClient(org.FinanceManager, "FinanceManager")
            .GetAsync("/api/v1/reports/outstanding-advances")).ShouldSucceedAsync();

        var totals = report.GetProperty("totals");
        Ngn(totals, "outstandingNgn").Should().BeGreaterThanOrEqualTo(250_000m);
        totals.GetProperty("overdueCount").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var row = report.GetProperty("advances").EnumerateArray()
            .Single(a => a.GetString("requestNumber") == advanceNumber);

        row.GetProperty("balanceNgn").GetDecimal().Should().Be(250_000m,
            "nothing has been retired against it, so the whole advance is outstanding");
        row.GetProperty("daysOverdue").GetInt32().Should().Be(9);
        row.GetProperty("isOverdue").GetBoolean().Should().BeTrue();
        row.GetString("requester").Should().Be(org.Requester.FullName);

        // Sorted by lateness, not by size. A large advance retired on time is
        // not a problem; a small one three weeks late is the start of one.
        var days = report.GetProperty("advances").EnumerateArray()
            .Select(a => a.GetProperty("daysOverdue").GetInt32())
            .ToList();

        days.Should().BeInDescendingOrder("the rows worth chasing must be at the top");
    }

    /// <summary>
    /// A role-gated state has no individual holder by design, and reporting
    /// that as "unassigned" would be both wrong and alarming.
    /// </summary>
    [Fact]
    public async Task The_pipeline_names_a_person_or_a_desk_but_never_nobody()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "RPT-PIPE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        // One waiting on a person.
        var atDeptHead = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Awaiting the head", new DateOnly(2026, 3, 1), 15_000m));

        // One waiting on a desk.
        var atCostControl = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Awaiting Cost Control", new DateOnly(2026, 3, 1), 25_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), atCostControl, "VERIFY"))
            .ShouldSucceedAsync();

        var deptHeadNumber = await RequestNumberOfAsync(atDeptHead);
        var costControlNumber = await RequestNumberOfAsync(atCostControl);

        var report = await (await Fixture.CreateClient(org.DirectorOfFinance, "DirectorOfFinance")
            .GetAsync("/api/v1/reports/pipeline")).ShouldSucceedAsync();

        var rows = report.GetProperty("requests").EnumerateArray().ToList();

        // Every row carries the date it stopped being a draft.
        //
        // Request.SubmittedAt existed from the first migration -- entity, EF
        // configuration, guard field, three DTOs and a composite index -- and
        // nothing ever wrote to it. This report was the first thing to filter
        // on it, and returned nothing at all. Asserted here so it cannot go
        // quiet again.
        report.GetProperty("totals").GetProperty("count").GetInt32().Should().BeGreaterThan(0,
            "a request that has been submitted must appear in the pipeline");

        var personRow = rows.Single(r => r.GetString("requestNumber") == deptHeadNumber);
        personRow.GetString("currentState").Should().Be("DEPT_HEAD");
        personRow.GetString("holder").Should().Be(org.DeptHead.FullName);
        personRow.GetProperty("holderIsRole").GetBoolean().Should().BeFalse();

        var deskRow = rows.Single(r => r.GetString("requestNumber") == costControlNumber);
        deskRow.GetString("currentState").Should().Be("COST_CONTROL_VERIFY");
        deskRow.GetString("holder").Should().Be("CostControlOfficer",
            "the state is held by a role, and naming the desk is the honest answer");
        deskRow.GetProperty("holderIsRole").GetBoolean().Should().BeTrue();

        rows.Should().NotContain(r => r.GetString("holder") == null,
            "an open request nobody holds is a defect, and this report is where it should become visible");
    }

    private async Task<string> RequestNumberOfAsync(Guid requestId) =>
        await WithDbAsync(async db => await db.Requests
            .AsNoTracking()
            .Where(r => r.RequestId == requestId)
            .Select(r => r.RequestNumber)
            .SingleAsync());
}
