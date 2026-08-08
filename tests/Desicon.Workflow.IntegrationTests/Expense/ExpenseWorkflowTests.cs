using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

public sealed class ExpenseWorkflowTests : IntegrationTestBase
{
    public ExpenseWorkflowTests(WorkflowApiFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Happy_path_drives_a_claim_from_draft_to_closed()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-HAPPY"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Taxi to airport", new DateOnly(2026, 1, 3), 5_000m),
            TestData.ExpenseLine("Hotel", new DateOnly(2026, 1, 3), 3_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-0001");
        await WorkflowSteps.DriveExpenseToAwaitingPaymentAsync(Fixture, org, id, "JV-0001", 8_000m);

        // EXECUTE_PAYMENT through its dedicated endpoint.
        //
        // This assertion used to read the other way round: it pinned
        // paymentReference and paymentDate as Null, because the transition
        // declares them in "captures" and nothing anywhere copied captured
        // fields onto the entity -- they reached the audit event's PayloadJson
        // and stopped. The state advanced, so the claim looked paid while the
        // columns recording how it was paid stayed empty. /execute-payment now
        // stages both before the transition runs, and this asserts that.
        var financeOfficerClient = Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer");
        await (await WorkflowSteps.ExecutePaymentAsync(financeOfficerClient, id, "PMT-0001"))
            .ShouldSucceedAsync();

        var afterPayment = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterPayment.GetProperty("currentState").GetString().Should().Be("AWAITING_ACK");
        afterPayment.GetProperty("paymentReference").GetString().Should().Be("PMT-0001");
        afterPayment.GetProperty("paymentDate").ValueKind.Should()
            .NotBe(System.Text.Json.JsonValueKind.Null, "a paid claim must record when it was paid");

        // Beneficiary == the requester here (self-beneficiary expense claim),
        // and "Beneficiary" is one of the two self-service resolvers exempted
        // from the self-approval policy check.
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.Requester), id, "ACKNOWLEDGE"))
            .ShouldSucceedAsync();

        var final = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        final.GetProperty("currentState").GetString().Should().Be("CLOSED");
        final.GetProperty("closedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Line_manager_can_return_a_claim_for_correction_and_requester_can_resubmit_it()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-RETURN"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Taxi", new DateOnly(2026, 1, 3), 4_000m));

        var returned = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.LineManager), id, "RETURN", comment: "Please attach the receipt image."))
            .ShouldSucceedAsync();
        returned.GetProperty("toState").GetString().Should().Be("RETURNED");

        var afterReturn = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterReturn.GetProperty("currentState").GetString().Should().Be("RETURNED");
        afterReturn.GetProperty("revisionNumber").GetInt32().Should().Be(1);

        var resubmitted = await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.Requester), id, "RESUBMIT"))
            .ShouldSucceedAsync();
        resubmitted.GetProperty("toState").GetString().Should().Be("LINE_MANAGER");

        var afterResubmit = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterResubmit.GetProperty("revisionNumber").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Line_manager_can_reject_a_claim_and_it_closes_as_terminal()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-REJECT"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Taxi", new DateOnly(2026, 1, 3), 4_000m));

        var rejected = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.LineManager), id, "REJECT", comment: "Not a valid business expense."))
            .ShouldSucceedAsync();
        rejected.GetProperty("toState").GetString().Should().Be("REJECTED");

        var after = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        after.GetProperty("currentState").GetString().Should().Be("REJECTED");
        after.GetProperty("closedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Sla_due_date_is_computed_on_entering_a_gated_state_but_no_automatic_escalation_occurs()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-SLA"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Taxi", new DateOnly(2026, 1, 3), 4_000m));

        var enteredLineManagerAt = Fixture.TimeProvider.GetUtcNow();

        DateTimeOffset expectedDueAt;
        using (var scope = Fixture.CreateScope())
        {
            var clock = scope.ServiceProvider.GetRequiredService<IWorkflowClock>();
            expectedDueAt = clock.AddWorkingHours(enteredLineManagerAt, 24, "NG_STANDARD");
        }

        var afterSubmit = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterSubmit.GetProperty("currentState").GetString().Should().Be("LINE_MANAGER");
        afterSubmit.GetProperty("slaDueAt").GetDateTimeOffset().Should().BeCloseTo(expectedDueAt, TimeSpan.FromSeconds(1));

        var originalActorId = afterSubmit.GetGuid("currentActorId");

        // Backdate the persisted SlaDueAt directly rather than advancing the
        // shared FakeTimeProvider: WorkflowApiFixture.TimeProvider is
        // assembly-wide, and Microsoft.Extensions.Time.Testing.FakeTimeProvider
        // refuses to ever move backward (SetUtcNow throws
        // ArgumentOutOfRangeException on any earlier value), so a forward
        // Advance() here could never be undone for whichever test runs next
        // in this collection. Production has no BackgroundService/
        // IHostedService anywhere that reads SlaDueAt -- WorkflowEngine only
        // computes and stores it -- so simulating "well past the due date"
        // by writing a stale SlaDueAt straight to the row proves the same
        // gap: nothing reassigns CurrentActorId to the state's escalateTo
        // target, and EscalationCount is reset to 0 on every transition and
        // never incremented outside one.
        await WithDbAsync(async db =>
        {
            var request = await db.Requests.FirstAsync(r => r.RequestId == id);
            request.SlaDueAt = enteredLineManagerAt.AddDays(-10);
            await db.SaveChangesAsync();
        });

        var afterSlaBreach = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterSlaBreach.GetProperty("currentState").GetString().Should().Be("LINE_MANAGER");
        afterSlaBreach.GetGuid("currentActorId").Should().Be(originalActorId);
    }

    [Fact]
    public async Task Negative_net_payable_routes_approval_to_refund_due_and_confirm_refund_returns_it_to_posting()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-REFUND"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Taxi", new DateOnly(2026, 1, 3), 4_000m));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, "TN-0002");

        // No HTTP path can set AdvanceAmountNgn on a fresh (non-retirement)
        // claim -- it is only ever populated by AdvanceRetirementEndpoints
        // when a claim retires a linked cash advance. Setting it directly
        // is the only way to reach the NetPayableNgn < 0 branch for a claim
        // that never went through that flow.
        await WithDbAsync(async db =>
        {
            var expense = await db.ExpenseRequests.FirstAsync(e => e.RequestId == id);
            expense.AdvanceAmountNgn = 4_500m;
            await db.SaveChangesAsync();
        });

        var approved = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();
        approved.GetProperty("toState").GetString().Should().Be("REFUND_DUE");

        var afterApprove = await (await Fixture.CreateClient(org.Requester)
            .GetAsync($"/api/v1/requests/{id}")).ShouldSucceedAsync();
        afterApprove.GetProperty("netPayableNgn").GetDecimal().Should().Be(-500m);
        afterApprove.GetProperty("isRefundDue").GetBoolean().Should().BeTrue();

        var confirmed = await (await WorkflowSteps.ConfirmRefundAsync(
                Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, 500m))
            .ShouldSucceedAsync();
        confirmed.GetProperty("toState").GetString().Should().Be("POSTING");
    }

    [Fact]
    public async Task Finance_verify_return_is_available_only_when_receipts_are_incomplete()
    {
        // Department.Code is nvarchar(20) and CreateOrgChartAsync appends a
        // "-" plus a 6-char suffix, so the prefix itself must stay <= 13
        // chars ("EXP-INCOMPLETE" alone was already 14, tipping the combined
        // code over the column limit and failing every test that used it).
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "EXP-INCOMPLT"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Incomplete",
            TestData.ExpenseLine("Taxi", new DateOnly(2026, 1, 3), 4_000m));

        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), id, "VERIFY")).ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY")).ShouldSucceedAsync();

        var returned = await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.FinanceOfficer, "FinanceOfficer"), id, "RETURN",
                comment: "Receipts incomplete."))
            .ShouldSucceedAsync();
        returned.GetProperty("toState").GetString().Should().Be("RETURNED");
    }
}
