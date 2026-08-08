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

        // The claim is at LINE_MANAGER. The definition gives that state three
        // transitions -- VERIFY, RETURN, REJECT -- all resolved to the
        // requester's line manager.
        var forManager = await (await Fixture.CreateClient(org.LineManager)
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
    /// AUTHORISE's guard reads BeneficiaryHasBankDetails, which is not a
    /// column on the request: it is copied from the Beneficiary immediately
    /// before the guard runs. Evaluated against an unstaged entity the guard
    /// always sees false, so a correct claim would be offered no Authorise
    /// button and the payment would stall with nothing to explain why.
    ///
    /// This drives the same claim twice through the same state -- once with
    /// the beneficiary's account details missing, once with them present --
    /// and asserts the answer changes. If the staging were dropped both halves
    /// would report the same thing, which is exactly what a test asserting
    /// only the happy case would fail to notice.
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

        var financeManagerClient = Fixture.CreateClient(org.FinanceManager, "FinanceManager");
        await (await WorkflowSteps.ActionAsync(financeManagerClient, id, "APPROVE")).ShouldSucceedAsync();

        await (await WorkflowSteps.CaptureGlLinesExpenseAsync(
                Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, "JV-ACT-C",
                WorkflowSteps.GlLine("Debit", "1000-EXP", 250_000m),
                WorkflowSteps.GlLine("Credit", "2000-BANK", 250_000m)))
            .ShouldSucceedAsync();

        // CreateEmployeeBeneficiaryAsync leaves bank details empty, so
        // HasBankDetails is false and a bank transfer cannot be authorised.
        var withoutBankDetails = await (await financeManagerClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        withoutBankDetails.GetProperty("currentState").GetString().Should().Be("AUTHORISATION");
        EnabledActionsOf(withoutBankDetails).Should()
            .NotContain("AUTHORISE", "a bank transfer to an account nobody recorded cannot be authorised");

        // Authorised but blocked, not absent -- and the refusal says why. The
        // Finance Manager holds the authority; what is missing is the account
        // number. Reporting that as "no actions" would send them to check
        // their own permissions.
        ActionsOf(withoutBankDetails).Should().Contain("AUTHORISE");
        BlockedReasonFor(withoutBankDetails, "AUTHORISE").Should()
            .NotBeNullOrWhiteSpace("a disabled action must say what to fix");

        await WithDbAsync(async db =>
        {
            var tracked = await db.Beneficiaries.FindAsync(beneficiary.Id);
            tracked!.BankName = "Test Commercial Bank";
            tracked.BankAccountNumber = "0123456789";
            tracked.BankDetailsSetAt = Fixture.TimeProvider.GetUtcNow();
            await db.SaveChangesAsync();
        });

        var withBankDetails = await (await financeManagerClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        EnabledActionsOf(withBankDetails).Should().Contain("AUTHORISE");

        // And the offer is honest: taking it succeeds.
        await (await WorkflowSteps.AuthorisePostingExpenseAsync(financeManagerClient, id)).ShouldSucceedAsync();
    }

    /// <summary>
    /// The inputer who posted must not be offered the authorisation of their
    /// own posting. The guard already refuses it; this pins that the screen is
    /// told so in advance rather than finding out on click, which is the
    /// difference between a control people trust and one they route around.
    /// </summary>
    [Fact]
    public async Task The_inputer_is_not_offered_authorisation_of_their_own_posting()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-D"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Fuel", new DateOnly(2026, 8, 8), 20_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-ACT-D");
        await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();

        // The Finance Officer both posts and holds the FinanceManager role
        // here -- the collision the maker-checker rule exists to catch.
        var inputer = Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer", "FinanceManager");

        await (await WorkflowSteps.CaptureGlLinesExpenseAsync(
                inputer, id, "JV-ACT-D",
                WorkflowSteps.GlLine("Debit", "1000-EXP", 20_000m),
                WorkflowSteps.GlLine("Credit", "2000-CASH", 20_000m)))
            .ShouldSucceedAsync();

        var forInputer = await (await inputer.GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        forInputer.GetProperty("currentState").GetString().Should().Be("AUTHORISATION");
        EnabledActionsOf(forInputer).Should().NotContain("AUTHORISE");
    }

    /// <summary>
    /// The deadlock this shape exists to prevent, pinned at the state where it
    /// was found in dev.
    ///
    /// FINANCE_VERIFY's VERIFY guard is
    ///   ReceiptStatus == 'Yes' && TreasuryNumber != null
    /// and the Treasury number is captured BY that action. An availability
    /// query that returns only guard-satisfied transitions therefore reports
    /// nothing at all here, and a screen keyed on that renders no field to
    /// enter the number into -- so the number can never be entered, and the
    /// claim cannot leave the state. Three capture steps were unreachable in
    /// the browser for exactly this reason, and EXP-2026-000005 stopped here.
    ///
    /// The RETURN alternative does not rescue it: its own guard requires
    /// ReceiptStatus == 'Incomplete', so on a claim with receipts the Finance
    /// Officer genuinely has no enabled action. Authorisation and guard
    /// satisfaction have to be reported separately or the state is a dead end.
    /// </summary>
    [Fact]
    public async Task An_action_blocked_only_by_data_it_captures_is_still_offered()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "ACTIONS-E"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Test ICT router", new DateOnly(2026, 8, 8), 200_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), id, "VERIFY"))
            .ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        var financeOfficerClient = Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer");
        var atFinanceVerify = await (await financeOfficerClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        atFinanceVerify.GetProperty("currentState").GetString().Should().Be("FINANCE_VERIFY");

        // No Treasury number yet, so nothing is takeable...
        EnabledActionsOf(atFinanceVerify).Should().BeEmpty();

        // ...but VERIFY is offered, with the definition's own guardMessage
        // attached, so the screen knows to render the field.
        ActionsOf(atFinanceVerify).Should().Contain("VERIFY");
        BlockedReasonFor(atFinanceVerify, "VERIFY").Should().Contain("Treasury");

        // And supplying it through the same generic endpoint the screen uses
        // makes the action live.
        await (await WorkflowSteps.ActionAsync(
                financeOfficerClient, id, "VERIFY",
                payload: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-ACT-E" }))
            .ShouldSucceedAsync();

        var afterVerify = await (await financeOfficerClient
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();

        afterVerify.GetProperty("currentState").GetString().Should().Be("FINANCE_APPROVE");
    }
}
