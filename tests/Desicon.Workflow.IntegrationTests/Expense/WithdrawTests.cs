using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Expense;

/// <summary>
/// That a requester can take back their own mistake, and that nobody else can
/// take it back for them.
/// </summary>
/// <remarks>
/// Workflow version 5. Until it, nothing could be withdrawn at all: no CANCEL,
/// WITHDRAW, ABANDON or VOID action existed in either module. A requester who
/// mistyped an amount or submitted twice had to ask an approver to reject their
/// own error.
///
/// Found by making the mistake. ADV-2026-000001 was raised in error on
/// 22 Aug 2026 and could only be cleared because the person who raised it
/// happened to be their own Head of Department. Anyone else would have had to
/// telephone somebody.
///
/// The interesting half is not that withdrawal works. It is the three things
/// that must NOT be true of it, which is what most of this file asserts:
/// withdrawal must not be available to other people, must not reach past the
/// first approval step, and must not be recorded as a rejection.
/// </remarks>
public sealed class WithdrawTests : IntegrationTestBase
{
    public WithdrawTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_requester_can_withdraw_their_own_request_before_anyone_has_acted()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WD-SELF"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Raised twice by mistake", new DateOnly(2026, 3, 4), 18_000m));

        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.Requester), id, "WITHDRAW",
            comment: "Submitted twice in error.")).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == id);

            request.CurrentState.Should().Be("WITHDRAWN");

            // The point of a terminal state. Without ClosedAt the request stays
            // in every "what is still open" query for ever -- which is exactly
            // the defect the pipeline report was built to end, and it would
            // have been reintroduced here by a state that merely looked final.
            request.ClosedAt.Should().NotBeNull(
                "WITHDRAWN is terminal, so the request must leave the open queue");
        });
    }

    /// <summary>
    /// Withdrawal is self-service or it is nothing.
    /// </summary>
    /// <remarks>
    /// A Head of Department already holds REJECT and RETURN on this request.
    /// Letting them also WITHDRAW it would let an approver dispose of somebody
    /// else's request while recording it as the requester's own decision --
    /// putting a change of mind in the trail against a person who never
    /// changed it.
    /// </remarks>
    [Fact]
    public async Task Nobody_else_can_withdraw_a_request_on_the_requesters_behalf()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WD-OTHER"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Site consumables", new DateOnly(2026, 3, 5), 22_000m));

        foreach (var other in new[] { org.DeptHead, org.FinanceManager, org.CostControlOfficer })
        {
            var refused = await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(other), id, "WITHDRAW", comment: "Not mine to withdraw.");

            refused.IsSuccessStatusCode.Should().BeFalse(
                "{0} did not raise this request; withdrawal is the requester's own decision", other.FullName);
        }

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("DEPT_HEAD", "every attempt was refused, so nothing moved");
        });
    }

    /// <summary>
    /// Once somebody else has spent time on it, it is no longer only yours.
    /// </summary>
    [Fact]
    public async Task A_request_cannot_be_withdrawn_once_it_has_passed_the_first_approval()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WD-LATE"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Approved already", new DateOnly(2026, 3, 6), 31_000m));

        // The Head of Department verifies it. Cost Control now owns it.
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), id, "VERIFY"))
            .ShouldSucceedAsync();

        var refused = await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.Requester), id, "WITHDRAW", comment: "Changed my mind.");

        refused.IsSuccessStatusCode.Should().BeFalse(
            "the Head of Department has already approved it -- withdrawing now would discard their " +
            "decision silently, and the way out of COST_CONTROL_VERIFY is a rejection somebody signs");

        await WithDbAsync(async db =>
        {
            var state = await db.Requests.AsNoTracking()
                .Where(r => r.RequestId == id).Select(r => r.CurrentState).SingleAsync();

            state.Should().Be("COST_CONTROL_VERIFY");
        });
    }

    /// <summary>
    /// A request handed back for correction is already the requester's again,
    /// so abandoning it should not require asking an approver to reject
    /// something they themselves returned.
    /// </summary>
    [Fact]
    public async Task A_returned_request_can_be_withdrawn_rather_than_resubmitted()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WD-RET"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Wrong cost centre", new DateOnly(2026, 3, 7), 9_500m));

        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.DeptHead), id, "RETURN",
            comment: "Wrong cost centre — please correct.")).ShouldSucceedAsync();

        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.Requester), id, "WITHDRAW",
            comment: "Not worth reclaiming; abandoning it.")).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == id);

            request.CurrentState.Should().Be("WITHDRAWN");
            request.ClosedAt.Should().NotBeNull();
        });
    }

    /// <summary>
    /// The whole reason WITHDRAWN exists as its own state.
    /// </summary>
    /// <remarks>
    /// Reusing REJECTED would have been a smaller change and would have read
    /// correctly on a status board. It would also have recorded, permanently
    /// and tamper-evidently, that an approver refused a request no approver
    /// ever saw a decision on.
    ///
    /// Attribution is the one thing this platform exists to get right — see
    /// docs/15 §1c on shared accounts. A trail that says the wrong person
    /// decided something is worse than one that says nothing.
    /// </remarks>
    [Fact]
    public async Task A_withdrawal_is_recorded_as_the_requesters_own_act_not_as_a_rejection()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "WD-TRAIL"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Duplicate", new DateOnly(2026, 3, 8), 12_000m));

        await (await WorkflowSteps.ActionAsync(
            Fixture.CreateClient(org.Requester), id, "WITHDRAW",
            comment: "Raised in error.")).ShouldSucceedAsync();

        await WithDbAsync(async db =>
        {
            var events = await db.AuditEvents.AsNoTracking()
                .Where(e => e.RequestId == id)
                .OrderBy(e => e.AuditEventId)
                .ToListAsync();

            var withdrawal = events.Single(e => e.ToState == "WITHDRAWN");

            withdrawal.ActorId.Should().Be(org.Requester.Id,
                "the person who withdrew it is the person who raised it");
            withdrawal.Reason.Should().Be("Raised in error.",
                "a withdrawal requires a comment, and the comment is the record of why");

            events.Should().NotContain(e => e.ToState == "REJECTED",
                "nobody rejected this request — recording one would attribute a decision to an " +
                "approver who never made it");
        });
    }
}
