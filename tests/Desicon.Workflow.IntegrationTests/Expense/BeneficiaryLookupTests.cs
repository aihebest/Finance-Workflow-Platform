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

    /// <summary>
    /// A name is not an identifier, and this is the field that decides who
    /// gets paid.
    /// </summary>
    /// <remarks>
    /// On 9 August 2026 a claim was raised against the wrong one of two
    /// employees sharing a display name, ran the full approval chain, and was
    /// paid. Nobody was careless: the picker showed a name, and so did every
    /// approval screen after it — including the Director of Finance's, whose
    /// whole function is authorising money to leave.
    ///
    /// Two assertions, because the defect had two halves. The picker must
    /// distinguish the two people at the point of choosing, and the detail
    /// response must name the payee at the point of approving.
    /// </remarks>
    [Fact]
    public async Task A_payee_is_identifiable_when_chosen_and_when_approved()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "BEN-DUP"));

        // The situation itself: a second employee with the requester's name.
        var namesake = await WithDbAsync(db =>
            TestData.CreateEmployeeAsync(db, org.Department, org.Requester.FullName));

        var mine = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        var theirs = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, namesake));

        var listed = await (await Fixture.CreateClient(org.Requester)
            .GetAsync("/api/v1/beneficiaries")).ShouldSucceedAsync();

        var candidates = listed.EnumerateArray()
            .Where(e => e.GetString("name") == org.Requester.FullName)
            .ToList();

        candidates.Should().HaveCount(2, "the fixture created two payees with one name");

        candidates.Select(c => c.GetString("staffNumber")).Should().OnlyHaveUniqueItems(
            "if the picker cannot tell them apart, neither can the person using it");
        candidates.Select(c => c.GetString("email")).Should().OnlyHaveUniqueItems();

        // And the approval screen must say which one was chosen.
        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, theirs.Id, "Yes",
            TestData.ExpenseLine("Router", new DateOnly(2026, 2, 5), 70_000m));

        var detail = await (await Fixture.CreateClient(org.LineManager)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        detail.TryGetProperty("beneficiary", out var payee).Should().BeTrue(
            "an approver authorising a payment must be shown who receives it");

        payee.GetString("name").Should().Be(namesake.FullName);
        payee.GetString("staffNumber").Should().Be(namesake.StaffNumber,
            "the name alone does not distinguish this payee from the requester");
        payee.GetString("email").Should().Be(namesake.Email);

        // The one that was NOT chosen must not be the one shown.
        payee.GetString("staffNumber").Should().NotBe(org.Requester.StaffNumber);
        mine.Id.Should().NotBe(theirs.Id);
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
