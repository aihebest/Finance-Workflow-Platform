using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Functions;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Sla;

/// <summary>
/// Verification that the audit chain verifier actually verifies.
///
/// The happy-path test alone would be worthless: a checker that always
/// returns "fine" passes it. Both directions are asserted here — an intact
/// chain passes, and a chain with a single edited row fails — because the
/// repo already has a documented history of controls that matched nothing
/// and reported success (conftest evaluating zero rules, the action-pinning
/// script nothing ran). This is the same class of check, so it gets the same
/// treatment its own decision log demands: verification in BOTH directions.
/// </summary>
public sealed class AuditChainVerificationTests : IntegrationTestBase
{
    public AuditChainVerificationTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task An_untampered_chain_passes()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "AUD-A"));

        await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(Fixture, org, "Chain intact", 2_000m);

        var verify = async () => await RunVerificationAsync();

        await verify.Should().NotThrowAsync();
    }

    /// <summary>
    /// Edits one field of one committed audit row directly, the way someone
    /// with database access would, and confirms the nightly check notices.
    /// The Reason column is chosen because changing it alters no workflow
    /// behaviour at all — the request still moves the same way, the inbox
    /// still shows the same items. Only the hash disagrees, which is exactly
    /// what tamper-evidence is for.
    /// </summary>
    [Fact]
    public async Task An_edited_audit_row_is_detected()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "AUD-B"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Chain tampered", 2_000m);

        await WithDbAsync(async db =>
        {
            var auditEvent = await db.AuditEvents
                .Where(e => e.RequestId == requestId)
                .OrderBy(e => e.AuditEventId)
                .FirstAsync();

            // Deliberately not re-sealed. Re-sealing would be the
            // sophisticated attack, and it is caught by the previous-hash
            // link check instead — but this is the ordinary one: someone
            // "correcting" a comment in SSMS.
            auditEvent.Reason = "Adjusted after the fact.";

            await db.SaveChangesAsync();
        });

        var verify = async () => await RunVerificationAsync();

        (await verify.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*modified or removed outside the application*");
    }

    private async Task RunVerificationAsync()
    {
        using var scope = Fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var verification = new AuditChainVerification(
            sp.GetRequiredService<WorkflowDbContext>(),
            sp.GetRequiredService<IWorkflowClock>(),
            NullLogger<AuditChainVerification>.Instance);

        await verification.RunAsync(new TimerInfo(), CancellationToken.None);
    }
}
