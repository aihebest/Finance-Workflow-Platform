using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// A line carrying an empty string for the allocation code it does not use.
///
/// This is what an HTML form sends: a text input the user never touched posts
/// "", not null. Nothing in the suite did that — every existing test omits
/// the unused field entirely — and the result was a defect no layer could see
/// on its own:
///
///   ExpenseLine.HasValidAllocation uses IsNullOrWhiteSpace, so ("", "CC-01")
///     is valid and the request passes application validation.
///   CK_ExpenseLine_Allocation uses IS NULL, so both columns are set and the
///     INSERT is rejected.
///
/// The caller got a 500 naming a constraint they had no way to satisfy: the
/// field was blank on screen, and blank was the problem.
/// </summary>
public sealed class BlankAllocationCodeTests : IntegrationTestBase
{
    public BlankAllocationCodeTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task An_empty_project_code_is_treated_as_unset()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "BLANK-A"));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var response = await WorkflowSteps.PostAsync(
            Fixture.CreateClient(org.Requester),
            "/api/v1/requests",
            new
            {
                ModuleKey = "EXPENSE",
                Payload = new
                {
                    ReceiptStatus = "Yes",
                    Lines = new[]
                    {
                        new
                        {
                            Description = "Router",
                            ExpenseDate = "2026-08-08",
                            ProjectCode = "",          // untouched input
                            CostCentreCode = "1103",
                            CurrencyCode = "NGN",
                            Amount = 330_000m,
                            FxRate = 1m,
                            FxRateDate = "2026-08-08",
                        },
                    },
                },
            });

        var created = await response.ShouldSucceedAsync();
        var requestId = created.GetGuid("requestId");

        var line = await WithDbAsync(async db => await db.ExpenseLines
            .AsNoTracking()
            .SingleAsync(l => l.RequestId == requestId));

        line.ProjectCode.Should().BeNull("blank means unset, and the CHECK constraint reads NULL");
        line.CostCentreCode.Should().Be("1103");
    }

    /// <summary>
    /// Blank in both is still invalid — the fix normalises what "unset" means,
    /// it does not weaken the rule. This must be refused as a 400 naming the
    /// line, not a 500 naming a constraint.
    /// </summary>
    [Fact]
    public async Task Blank_in_both_codes_is_still_refused()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "BLANK-B"));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var response = await WorkflowSteps.PostAsync(
            Fixture.CreateClient(org.Requester),
            "/api/v1/requests",
            new
            {
                ModuleKey = "EXPENSE",
                Payload = new
                {
                    ReceiptStatus = "Yes",
                    Lines = new[]
                    {
                        new
                        {
                            Description = "Router",
                            ExpenseDate = "2026-08-08",
                            ProjectCode = "",
                            CostCentreCode = "  ",
                            CurrencyCode = "NGN",
                            Amount = 1_000m,
                            FxRate = 1m,
                            FxRateDate = "2026-08-08",
                        },
                    },
                },
            });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("projectCode", "the refusal should name what to fix");
    }
}
