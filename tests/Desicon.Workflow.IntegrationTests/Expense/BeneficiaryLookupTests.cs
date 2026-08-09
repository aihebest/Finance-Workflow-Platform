using System.Text.Json;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// The beneficiary lookup exists so the DEL-AC-FRM-002 capture form can fill
/// its "Name of the Beneficiary" field, whose payload requires a
/// BeneficiaryId.
///
/// The assertion that matters is the negative one: bank details must not
/// appear. A lookup that quietly returned them would put every payee's
/// banking arrangements behind one authenticated GET, and nothing else in
/// the system would notice — the column is Always Encrypted at rest, which
/// protects it from a database reader, not from an API that decrypts and
/// serialises it.
/// </summary>
public sealed class BeneficiaryLookupTests : IntegrationTestBase
{
    public BeneficiaryLookupTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Lists_beneficiaries_without_exposing_bank_details()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "BEN-A"));

        await WithDbAsync(async db =>
        {
            db.Beneficiaries.Add(new Beneficiary
            {
                Type = BeneficiaryType.Vendor,
                Name = "Ubiquiti Supplies Ltd",
                BankName = "First Bank",
                BankAccountNumber = "0123456789",

                // Stamped as IBankDetailsAuditor would. The lookup derives
                // HasBankDetails from this rather than from the encrypted
                // account number, so a fixture that sets the columns without
                // the stamp would report a payee with bank details as
                // unpayable -- and the real write path always sets both.
                BankDetailsSetByUserId = org.CostControlOfficer.Id,
                BankDetailsSetAt = Fixture.TimeProvider.GetUtcNow(),
            });

            await db.SaveChangesAsync();
        });

        var response = await Fixture.CreateClient(org.Requester).GetAsync("/api/v1/beneficiaries");
        var json = await response.ShouldSucceedAsync();

        var raw = json.GetRawText();

        raw.Should().Contain("Ubiquiti Supplies Ltd");
        raw.Should().NotContain("0123456789", "an account number must never reach a picker");
        raw.Should().NotContain("First Bank", "the bank identifies where money goes");

        var first = json.EnumerateArray().First(e => e.GetString("name") == "Ubiquiti Supplies Ltd");

        first.GetString("type").Should().Be("Vendor");
        first.GetProperty("hasBankDetails").GetBoolean().Should().BeTrue(
            "a requester should see a payee cannot be paid before filling in eleven lines, not after");
        first.TryGetProperty("bankAccountNumber", out _).Should().BeFalse();
        first.TryGetProperty("bankName", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Search_narrows_the_list()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "BEN-B"));

        await WithDbAsync(async db =>
        {
            db.Beneficiaries.Add(new Beneficiary { Type = BeneficiaryType.Vendor, Name = "Alpha Traders" });
            db.Beneficiaries.Add(new Beneficiary { Type = BeneficiaryType.Vendor, Name = "Beta Logistics" });
            await db.SaveChangesAsync();
        });

        var response = await Fixture.CreateClient(org.Requester).GetAsync("/api/v1/beneficiaries?search=Alpha");
        var json = await response.ShouldSucceedAsync();

        var names = json.EnumerateArray().Select(e => e.GetString("name")).ToList();

        names.Should().Contain("Alpha Traders");
        names.Should().NotContain("Beta Logistics");
    }

    [Fact]
    public async Task Requires_authentication()
    {
        // No acting user: TestAuthHandler only authenticates when the client
        // carries the test identity header, so this exercises the
        // RequireAuthorization on the endpoint rather than trusting it.
        using var anonymous = Fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/beneficiaries");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
