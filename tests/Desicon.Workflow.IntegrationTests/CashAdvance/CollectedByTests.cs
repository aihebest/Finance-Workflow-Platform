using System.Net.Http.Json;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// That a cash advance can name whoever is collecting the cash, and that
/// naming them changes nothing about who owes it back.
/// </summary>
/// <remarks>
/// Asked for by Aihe on 10 September 2026: "it's just somebody that's gonna
/// collect the cash. That's all. The person can later share his bank details
/// with the treasury later."
///
/// So this is a name and not a payee, and the distinction is the reason this
/// class exists rather than a line in an existing test. DEL-AC-FRM-003 calls an
/// advance the recipient's personal liability until it is justified, and every
/// rule built on that reads <c>RequesterId</c>: My Advances, the retirement
/// clock, and the SUBMIT guard that refuses a second advance while one is
/// overdue. If writing a name here quietly moved any of those, a supervisor
/// drawing cash for a site team would stop being able to raise advances, and
/// the person who actually did would never see the one they are liable for.
///
/// Free text with no lookup is the requirement, not a shortcut. A driver or a
/// casual worker exists nowhere in this system; making them a Beneficiary first
/// would defeat the purpose. The expense claim's beneficiary is a different
/// thing entirely -- a payment target with an account number and a
/// maker-checker rule over who set it.
/// </remarks>
public sealed class CollectedByTests : IntegrationTestBase
{
    public CollectedByTests(WorkflowApiFixture fixture) : base(fixture) { }

    private static JsonContent Draft(string purpose, decimal amount, string? collectedBy) =>
        JsonContent.Create(new
        {
            moduleKey = "CASH_ADVANCE",
            payload = new
            {
                purpose,
                beneficiaryName = collectedBy,
                allocationType = "CostCentre",
                projectCode = (string?)null,
                costCentreCode = "CC-01",
                stationScope = "InStation",
                hasSupportingDocuments = true,
                lines = new[]
                {
                    new
                    {
                        description = purpose,
                        currencyCode = "NGN",
                        amount,
                        fxRate = 1.0m,
                        fxRateDate = new DateOnly(2026, 9, 10)
                    }
                }
            }
        });

    [Fact]
    public async Task An_advance_can_name_somebody_who_exists_nowhere_in_this_system()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "COLLECT"));
        var requester = Fixture.CreateClient(org.Requester);

        var created = await (await requester.PostAsync(
            "/api/v1/requests", Draft("Site diesel", 80_000m, "Musa Danladi (driver)")))
            .ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.AsNoTracking()
                .SingleAsync(a => a.RequestId == id);

            advance.BeneficiaryName.Should().Be("Musa Danladi (driver)");

            advance.RequesterId.Should().Be(org.Requester.Id,
                "naming a collector does not hand the advance to them -- it stays the " +
                "liability of whoever raised it");
        });
    }

    /// <summary>
    /// The name reaches the desk that needs it.
    /// </summary>
    /// <remarks>
    /// Treasury releases the cash. If the name were stored and not returned,
    /// the field would be exactly the tick box this platform keeps removing:
    /// captured, stored, and read by nothing.
    /// </remarks>
    [Fact]
    public async Task Treasury_can_see_who_is_collecting_before_releasing_the_cash()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "COLLECT-T"));
        var requester = Fixture.CreateClient(org.Requester);

        var created = await (await requester.PostAsync(
            "/api/v1/requests", Draft("Site diesel", 80_000m, "Musa Danladi (driver)")))
            .ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        var detail = await (await Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer")
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        detail.GetString("beneficiaryName").Should().Be("Musa Danladi (driver)");
    }

    /// <summary>
    /// Left blank, it means the requester is collecting it themselves.
    /// </summary>
    /// <remarks>
    /// Stored as null rather than "": the ordinary case must be one value and
    /// not two, or every reader has to remember to check for both. Same
    /// normalisation the allocation codes get, and for the same reason -- a
    /// blank string from an HTML input is what introduced that distinction in
    /// the first place.
    /// </remarks>
    [Fact]
    public async Task Leaving_it_blank_records_nobody_rather_than_an_empty_name()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "COLLECT-N"));
        var requester = Fixture.CreateClient(org.Requester);

        var created = await (await requester.PostAsync(
            "/api/v1/requests", Draft("Office consumables", 12_000m, "   ")))
            .ShouldSucceedAsync();

        var id = created.GetGuid("requestId");

        await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.AsNoTracking()
                .SingleAsync(a => a.RequestId == id);

            advance.BeneficiaryName.Should().BeNull();
        });
    }

    /// <summary>
    /// And it does not follow the advance into the retirement claim.
    /// </summary>
    /// <remarks>
    /// The retirement claim pays the requester if anything is owed, because the
    /// requester is who is out of pocket -- they answer for the money whoever
    /// carried it. This asserts the retirement path did not quietly start
    /// paying the collector, which would be a real payment going to a name
    /// nobody verified.
    /// </remarks>
    [Fact]
    public async Task The_retirement_claim_still_belongs_to_the_requester()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "COLLECT-R"));
        await WithDbAsync(db => TestData.SetBankDetailsAsync(db, org.Requester));

        var id = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Site diesel", 80_000m, "TN-CB-1", "BC-CB-1",
            Fixture.TimeProvider.GetUtcNow());

        var draft = await (await WorkflowSteps.RetireAdvanceAsync(
            Fixture.CreateClient(org.Requester), id)).ShouldSucceedAsync();

        var claimId = draft.GetGuid("expenseRequestId");

        await WithDbAsync(async db =>
        {
            var claim = await db.ExpenseRequests.AsNoTracking()
                .SingleAsync(e => e.RequestId == claimId);

            claim.RequesterId.Should().Be(org.Requester.Id);

            var beneficiary = await db.Beneficiaries.AsNoTracking()
                .SingleAsync(b => b.Id == claim.BeneficiaryId);

            beneficiary.EmployeeId.Should().Be(org.Requester.Id,
                "the retirement pays back the person who answered for the money, not whoever " +
                "was handed it at the counter");
        });
    }
}
