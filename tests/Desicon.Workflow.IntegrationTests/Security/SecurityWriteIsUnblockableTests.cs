using Desicon.Workflow.Domain.Security;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Security;

/// <summary>
/// ISecurityEventWriter opens its own connection so that a record of a
/// refusal survives the business transaction being rolled back. That is the
/// central claim of the design, and nothing verified it.
///
/// It was also false. SecurityEventConfiguration declared a foreign key from
/// SecurityEvents.RequestId to Requests while its own remarks said it did
/// not — and that constraint made the guarantee impossible to honour: on a
/// separate connection, a request row inside an uncommitted (or rolled back)
/// transaction is invisible, so the insert fails on the foreign key and the
/// security record is lost in exactly the case the separate connection
/// exists for.
///
/// These tests pin the property rather than the implementation. If someone
/// reintroduces a constraint on RequestId — reasonably, since a dangling
/// reference looks untidy — these fail and explain why it cannot be there.
/// </summary>
public sealed class SecurityWriteIsUnblockableTests : IntegrationTestBase
{
    public SecurityWriteIsUnblockableTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_security_event_can_be_written_for_a_request_that_does_not_exist()
    {
        var orphanRequestId = Guid.NewGuid();

        using var scope = Fixture.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<ISecurityEventWriter>();

        await writer.WriteAsync(
            new SecurityEvent
            {
                RequestId = orphanRequestId,
                ModuleKey = "EXPENSE",
                Action = "SET_BANK_DETAILS",
                Reason = "BankDetailsChanged",
                Detail = "Written while the request was still uncommitted.",
                AttemptedByUserId = Guid.NewGuid(),
                OccurredAtUtc = Fixture.TimeProvider.GetUtcNow(),
            },
            CancellationToken.None);

        var written = await WithDbAsync(async db => await db.SecurityEvents
            .AsNoTracking()
            .AnyAsync(e => e.RequestId == orphanRequestId));

        written.Should().BeTrue(
            "a security write must never be refused because the request it names is not committed");
    }

    /// <summary>
    /// The scenario the separate connection was built for: work is rolled
    /// back, and the record of what was attempted remains.
    /// </summary>
    [Fact]
    public async Task A_security_event_survives_the_business_transaction_rolling_back()
    {
        var requestId = Guid.NewGuid();

        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<ISecurityEventWriter>();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await writer.WriteAsync(
                new SecurityEvent
                {
                    RequestId = requestId,
                    ModuleKey = "EXPENSE",
                    Action = "VERIFY",
                    Reason = "NotAuthorised",
                    Detail = "Denied, then the transaction was rolled back.",
                    AttemptedByUserId = Guid.NewGuid(),
                    OccurredAtUtc = Fixture.TimeProvider.GetUtcNow(),
                },
                CancellationToken.None);

            await transaction.RollbackAsync();
        }

        var survived = await WithDbAsync(async db2 => await db2.SecurityEvents
            .AsNoTracking()
            .AnyAsync(e => e.RequestId == requestId));

        survived.Should().BeTrue(
            "the whole reason ISecurityEventWriter uses its own connection is that a denial " +
            "must outlive the transaction that was denied");
    }
}
