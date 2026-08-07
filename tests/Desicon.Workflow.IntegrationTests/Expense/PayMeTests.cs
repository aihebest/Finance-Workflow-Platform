using System.Net;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// Raising a claim payable to yourself, which DEL-AC-FRM-002 treats as the
/// ordinary case ("Please issue payment in favour of company/staff").
///
/// It was impossible until now. A Beneficiary row is only ever created by the
/// advance-retirement path, so a requester with no prior advance had nobody
/// to select — including themselves — while the payload required an explicit
/// BeneficiaryId. The form looked complete and could not be submitted.
/// </summary>
public sealed class PayMeTests : IntegrationTestBase
{
    public PayMeTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Omitting_the_beneficiary_pays_the_requester()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYME-A"));
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
                            Description = "Fuel, Lagos site visit",
                            ExpenseDate = "2026-08-03",
                            CostCentreCode = "CC-01",
                            CurrencyCode = "NGN",
                            Amount = 12_000m,
                            FxRate = 1m,
                            FxRateDate = "2026-08-03",
                        },
                    },
                },
            });

        var created = await response.ShouldSucceedAsync();
        var requestId = created.GetGuid("requestId");

        // Resolved server-side to a beneficiary linked back to the requester,
        // rather than left empty or invented by the client.
        var beneficiary = await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.AsNoTracking()
                .SingleAsync(e => e.RequestId == requestId);

            return await db.Beneficiaries.AsNoTracking()
                .SingleAsync(b => b.Id == expense.BeneficiaryId);
        });

        beneficiary.Type.Should().Be(BeneficiaryType.Employee);
        beneficiary.EmployeeId.Should().Be(org.Requester.Id);

        // Created through IBankDetailsAuditor, so the maker-checker stamp is
        // present. Without it, whoever set the bank details is unknown and
        // the payment-authorisation guard has nothing to compare against.
        beneficiary.BankDetailsSetByUserId.Should().NotBeNull();
        beneficiary.BankDetailsSetAt.Should().NotBeNull();
    }

    /// <summary>
    /// An employee with no bank details cannot be paid, and the claim is
    /// refused at creation rather than accepted and stranded at the payment
    /// stage. This is the failure that reads as a bug and is not one — the
    /// request is well-formed and the person exists; there is simply nowhere
    /// to send the money.
    /// </summary>
    [Fact]
    public async Task Paying_someone_with_no_bank_details_is_refused_with_a_reason()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "PAYME-B"));

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
                            Description = "Fuel",
                            ExpenseDate = "2026-08-03",
                            CostCentreCode = "CC-01",
                            CurrencyCode = "NGN",
                            Amount = 5_000m,
                            FxRate = 1m,
                            FxRateDate = "2026-08-03",
                        },
                    },
                },
            });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("bank details", "the refusal must say what is missing");
    }
}
