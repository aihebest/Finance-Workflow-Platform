using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using Desicon.Workflow.Infrastructure.Workflow;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Delegations;

/// <summary>
/// EmployeeActorResolver.ExpandWithDelegatesAsync: if employee B holds an
/// active Delegation from A, B is added to whatever actor set A resolved
/// into -- one level only, "a delegate for a delegate is out of scope".
/// These tests exercise that expansion over HTTP (the delegate acting as
/// themselves, never touching ActingUser.OnBehalfOf -- that path is
/// unreachable from HTTP; see ICurrentUserAccessor's own comment), plus the
/// separate defence-in-depth self-approval check in RequestActionService
/// .EvaluatePolicyViolation, which must compare the *effective* actor
/// (OnBehalfOf ?? UserId) against the request's RequesterId, not the nominal
/// caller -- the only way to observe that distinction is a direct in-process
/// RequestActionService.ExecuteAsync call with OnBehalfOf set explicitly,
/// since no HTTP endpoint can ever populate it.
/// </summary>
public sealed class DelegationAuthorizationTests : IntegrationTestBase
{
    public DelegationAuthorizationTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_delegate_can_act_in_place_of_the_person_who_delegated_to_them()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DEL-ACT"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        var now = Fixture.TimeProvider.GetUtcNow();

        var delegateEmployee = await WithDbAsync(db => TestData.CreateEmployeeAsync(db, org.Department, "Delegate of Line Manager"));
        await WithDbAsync(db => TestData.CreateDelegationAsync(
            db, org.LineManager, delegateEmployee, now.AddDays(-1), now.AddDays(1)));

        var claimId = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Delegated approval test", DateOnly.FromDateTime(now.Date), 1_000m));

        var verify = await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(delegateEmployee), claimId, "VERIFY"))
            .ShouldSucceedAsync();
        verify.GetString("toState").Should().Be("DEPT_HEAD");
    }

    [Fact]
    public async Task Delegation_is_not_transitive()
    {
        // A -> B -> C: B holds A's authority, so B can act. C only holds a
        // delegation from B, not from A, and the expansion is one level
        // only -- C must NOT be able to act in A's place.
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DEL-NONTRANS"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        var now = Fixture.TimeProvider.GetUtcNow();

        var delegateB = await WithDbAsync(db => TestData.CreateEmployeeAsync(db, org.Department, "First-level delegate"));
        var delegateC = await WithDbAsync(db => TestData.CreateEmployeeAsync(db, org.Department, "Second-level delegate"));
        await WithDbAsync(db => TestData.CreateDelegationAsync(db, org.LineManager, delegateB, now.AddDays(-1), now.AddDays(1)));
        await WithDbAsync(db => TestData.CreateDelegationAsync(db, delegateB, delegateC, now.AddDays(-1), now.AddDays(1)));

        var claimForB = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("First-level delegate can act", DateOnly.FromDateTime(now.Date), 1_000m));
        var claimForC = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Second-level delegate cannot act", DateOnly.FromDateTime(now.Date), 1_000m));

        var bVerify = await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(delegateB), claimForB, "VERIFY"))
            .ShouldSucceedAsync();
        bVerify.GetString("toState").Should().Be("DEPT_HEAD");

        var cVerify = await WorkflowSteps.ActionAsync(Fixture.CreateClient(delegateC), claimForC, "VERIFY");
        cVerify.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden,
            "a delegate-of-a-delegate is out of scope -- delegation expands one level only");
    }

    [Fact]
    public async Task A_delegate_cannot_approve_a_request_raised_by_the_person_they_are_delegating_for()
    {
        // FINANCE_VERIFY's VERIFY transition is a role-only spec (FinanceOfficer,
        // no identity resolver), so any FinanceOfficer -- including one acting
        // OnBehalfOf someone else -- clears WorkflowEngine's own authorisation
        // layer. RequestActionService.EvaluatePolicyViolation is what actually
        // blocks self-approval, and it must key off effectiveActorId
        // (OnBehalfOf ?? UserId), not actingUser.UserId: here the nominal
        // caller is org.FinanceOfficer (not the requester, so a nominal-only
        // check would wrongly allow this), but OnBehalfOf points at
        // org.Requester -- the request's own requester -- so it must be
        // blocked.
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DEL-SELF"));
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        var now = Fixture.TimeProvider.GetUtcNow();

        var claimId = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Effective-user self-approval test", DateOnly.FromDateTime(now.Date), 1_000m));
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.LineManager), claimId, "VERIFY")).ShouldSucceedAsync();
        await (await WorkflowSteps.ActionAsync(Fixture.CreateClient(org.DeptHead), claimId, "VERIFY")).ShouldSucceedAsync();

        // FINANCE_VERIFY's VERIFY guard ("TreasuryNumber != null") reads
        // straight off the tracked Request entity, not off CapturedFields --
        // RequestEndpoints.ExecuteActionAsync stages it onto the entity
        // before calling RequestActionService.ExecuteAsync for exactly this
        // reason (see its own comment). This test bypasses that HTTP
        // endpoint to get OnBehalfOf onto ActingUser, so it must stage
        // TreasuryNumber itself or the engine's guard rejects the transition
        // before EvaluatePolicyViolation ever runs.
        await WithDbAsync(async db =>
        {
            var request = await db.Requests.FirstAsync(r => r.RequestId == claimId);
            request.TreasuryNumber = "TN-DELEGATE-SELF";
            await db.SaveChangesAsync();
        });

        using var scope = Fixture.CreateScope();
        var actionService = scope.ServiceProvider.GetRequiredService<RequestActionService>();

        var actingUser = new ActingUser(
            UserId: org.FinanceOfficer.Id,
            Roles: new HashSet<string> { "FinanceOfficer" },
            OnBehalfOf: org.Requester.Id);

        var result = await actionService.ExecuteAsync(
            claimId, actingUser,
            new TransitionRequest("VERIFY", CapturedFields: new Dictionary<string, object?> { ["TreasuryNumber"] = "TN-DELEGATE-SELF" }));

        result.Outcome.Should().Be(TransitionOutcome.PolicyViolation,
            "effectiveActorId (OnBehalfOf) equals this request's RequesterId, so self-approval must be " +
            "blocked even though the nominal caller (org.FinanceOfficer) is not the requester");
    }
}
