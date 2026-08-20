using System.Text.Json;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// What the caller may do next, as reported by GET /api/v1/requests/{id}.
///
/// This is the field the approval screen renders its buttons from, and until
/// now the API never sent it. WorkflowEngine.GetAvailableTransitionsAsync
/// existed, carried a docstring saying it was "used to drive the UI's action
/// buttons", and had no callers anywhere in the solution. The browser read
/// `detail.availableActions`, got undefined, fell back to an empty array and
/// rendered nothing -- in every state, for every user, with no error raised at
/// any layer. The API returned 200, the screen drew a page, and the only
/// symptom was an approval that could not be given.
///
/// The suite could not see it either, because every existing test drives the
/// workflow by POSTing action names it already knows. Nothing had ever asked
/// the API which actions it would offer.
/// </summary>
public sealed class AvailableActionsTests : IntegrationTestBase
{
    public AvailableActionsTests(WorkflowApiFixture fixture) : base(fixture) { }

    /// <summary>Actions the caller is authorised for, enabled or not.</summary>
    private static string[] ActionsOf(JsonElement detail) =>
        detail.GetProperty("availableActions").EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()!)
            .ToArray();

    /// <summary>Actions they can take right now.</summary>
    private static string[] EnabledActionsOf(JsonElement detail) =>
        detail.GetProperty("availableActions").EnumerateArray()
            .Where(e => e.GetProperty("isEnabled").GetBoolean())
            .Select(e => e.GetProperty("action").GetString()!)
            .ToArray();

    private static string? BlockedReasonFor(JsonElement detail, string action) =>
        detail.GetProperty("availableActions").EnumerateArray()
            .Where(e => e.GetProperty("action").GetString() == action)
            .Select(e => e.GetProperty("blockedReason").GetString())
            .FirstOrDefault();

    [Fact]
    public async Task The_resolved_approver_is_offered_the_actions_that_state_allows()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-A"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Router", new DateOnly(2026, 8, 8), 40_000m));

        // The claim is at DEPT_HEAD. The definition gives that state three
        // transitions -- VERIFY, RETURN, REJECT -- all resolved to the
        // requester's line manager.
        var forManager = await (await Fixture.CreateClient(org.DeptHead)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        ActionsOf(forManager).Should().BeEquivalentTo("VERIFY", "RETURN", "REJECT");
    }

    [Fact]
    public async Task The_requester_waiting_on_someone_else_is_offered_nothing()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-B"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Router", new DateOnly(2026, 8, 8), 40_000m));

        // The requester can read their own claim -- and must not be offered a
        // way to verify it. An empty list here is the substance of the
        // separation of duties the paper form achieved with two signature
        // boxes.
        var forRequester = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        ActionsOf(forRequester).Should().BeEmpty();
    }

    /// <summary>
    /// The reason availability is computed through the same staging as
    /// execution, rather than from the definition alone.
    ///
    /// EXECUTE_PAYMENT's guard reads BeneficiaryHasBankDetails, which is not a
    /// column on the request: it is copied from the Beneficiary immediately
    /// before the guard runs. Evaluated against an unstaged entity the guard
    /// always sees false, so a correct claim would be offered no payment action
    /// and the money would stall with nothing to explain why.
    ///
    /// This drives the same claim twice through the same state -- once with the
    /// beneficiary's account details missing, once with them present -- and
    /// asserts the answer changes. If the staging were dropped, both halves
    /// would report the same thing, which is exactly what a test asserting only
    /// the happy case would fail to notice.
    ///
    /// The guard lived on AUTHORISE until definition version 2 moved posting to
    /// Business Central. It now sits on the transition where money actually
    /// leaves, which is where it always belonged.
    /// </summary>
    [Fact]
    public async Task Availability_reflects_guard_fields_that_are_not_columns()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-C"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        // Above PAYMENT_METHOD_THRESHOLD_NGN (30,000), so PaymentMethod
        // resolves to BankTransfer and the bank-details clause is the one that
        // decides the guard.
        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Generator hire", new DateOnly(2026, 8, 8), 250_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-ACT-C");
        await WorkflowSteps.DriveExpenseToAwaitingPaymentAsync(Fixture, org, id, "BC-ACT-C");

        var treasuryClient = Fixture.CreateClient(org.TreasuryOfficer, "TreasuryOfficer");

        // CreateEmployeeBeneficiaryAsync leaves bank details empty, so
        // HasBankDetails is false and a bank transfer cannot be paid.
        var withoutBankDetails = await (await treasuryClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        withoutBankDetails.GetProperty("currentState").GetString().Should().Be("AWAITING_PAYMENT");
        EnabledActionsOf(withoutBankDetails).Should()
            .NotContain("EXECUTE_PAYMENT", "a transfer to an account nobody recorded cannot be made");

        // Authorised but blocked, not absent -- and the refusal says why. The
        // Accounts Officer holds the authority; what is missing is the account
        // number. Reporting that as "no actions" would send her to check her
        // own permissions.
        ActionsOf(withoutBankDetails).Should().Contain("EXECUTE_PAYMENT");
        BlockedReasonFor(withoutBankDetails, "EXECUTE_PAYMENT").Should()
            .NotBeNullOrWhiteSpace("a disabled action must say what to fix");

        await WithDbAsync(async db =>
        {
            var tracked = await db.Beneficiaries.FindAsync(beneficiary.Id);
            tracked!.BankName = "Test Commercial Bank";
            tracked.BankAccountNumber = "0123456789";
            tracked.BankDetailsSetAt = Fixture.TimeProvider.GetUtcNow();
            await db.SaveChangesAsync();
        });

        var withBankDetails = await (await treasuryClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        EnabledActionsOf(withBankDetails).Should().Contain("EXECUTE_PAYMENT");

        // And the offer is honest: taking it succeeds.
        await (await WorkflowSteps.ExecutePaymentAsync(treasuryClient, id, "PMT-ACT-C"))
            .ShouldSucceedAsync();
    }

    /// <summary>
    /// The Director of Finance's gate cannot be satisfied by anyone else.
    ///
    /// This replaces a test that pinned the inputer/authoriser maker-checker on
    /// GL posting. That control moved to Business Central with the posting
    /// itself, so the equivalent question here is whether the payment gate is
    /// real: an Accounts Manager holding every other Finance role must still be
    /// unable to authorise a payment, because DMD_APPROVAL resolves to one role
    /// and one role only. A gate that a sufficiently senior person can step
    /// around is not a gate.
    /// </summary>
    [Fact]
    public async Task Only_the_director_of_finance_can_approve_a_payment()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-D"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Fuel", new DateOnly(2026, 8, 8), 20_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-ACT-D");

        // Every Finance role except the one that matters.
        var accountsManager = Fixture.CreateClient(
            org.FinanceManager, "FinanceManager", "CostControlOfficer", "TreasuryOfficer");
        await (await WorkflowSteps.ActionAsync(accountsManager, id, "APPROVE")).ShouldSucceedAsync();

        var forAccountsManager = await (await accountsManager
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        forAccountsManager.GetProperty("currentState").GetString().Should().Be("DMD_APPROVAL");
        ActionsOf(forAccountsManager).Should()
            .BeEmpty("holding every other Finance role must not substitute for the Director of Finance");

        // The DMD, and only the DMD, moves it on.
        await (await WorkflowSteps.ApproveAsDirectorOfFinanceAsync(Fixture, org, id)).ShouldSucceedAsync();

        var afterDmd = await (await accountsManager.GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterDmd.GetProperty("currentState").GetString().Should().Be("AWAITING_POSTING");
    }
}
