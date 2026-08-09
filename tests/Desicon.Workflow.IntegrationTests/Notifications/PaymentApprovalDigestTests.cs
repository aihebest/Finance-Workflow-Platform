using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Functions;
using Desicon.Workflow.Infrastructure.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Notifications;

/// <summary>
/// The Director of Finance's weekday digest.
///
/// Written because the alternative was shipping a notification nobody had
/// watched work — which is how most of this platform's defects got here. A
/// digest that silently sends nothing is indistinguishable from a quiet week,
/// and the person it fails is the one approving every payment Desicon makes.
/// </summary>
public sealed class PaymentApprovalDigestTests : IntegrationTestBase
{
    private const string DmdMailbox = "dmd@desicon.test";

    public PaymentApprovalDigestTests(WorkflowApiFixture fixture) : base(fixture) { }

    /// <summary>
    /// Drives a claim to DMD_APPROVAL: the state the digest reports on.
    /// </summary>
    private async Task<Guid> ClaimAwaitingPaymentApprovalAsync(OrgChart org, decimal amount, string prefix)
    {
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));

        var id = await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Digest line", new DateOnly(2026, 8, 9), amount));

        await WorkflowSteps.DriveExpenseToFinanceApproveAsync(Fixture, org, id, $"TN-{prefix}");
        await (await WorkflowSteps.ActionAsync(
                Fixture.CreateClient(org.FinanceManager, "FinanceManager"), id, "APPROVE"))
            .ShouldSucceedAsync();

        return id;
    }

    /// <param name="mailbox">
    /// Null configures no mailbox for the role, which is the case that must not
    /// silently succeed.
    /// </param>
    private async Task<RecordingNotificationSender> RunDigestAsync(string? mailbox = DmdMailbox)
    {
        using var scope = Fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var options = new NotificationOptions
        {
            ApplicationBaseUrl = "https://finance.desicon.test",
            SenderMailbox = "finance-workflow@desicon.test"
        };

        if (mailbox is not null)
        {
            options.RoleMailboxes["DirectorOfFinance"] = mailbox;
        }

        var sender = new RecordingNotificationSender();

        var sweep = new PaymentApprovalDigestSweep(
            sp.GetRequiredService<WorkflowDbContext>(),
            sp.GetRequiredService<IWorkflowClock>(),
            options,
            new NotificationRenderer(options),
            sender,
            NullLogger<PaymentApprovalDigestSweep>.Instance);

        await sweep.RunAsync(new TimerInfo(), CancellationToken.None);

        return sender;
    }

    [Fact]
    public async Task Everything_awaiting_payment_approval_appears_in_one_message()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DIGEST-A"));

        await ClaimAwaitingPaymentApprovalAsync(org, 40_000m, "DGA1");
        await ClaimAwaitingPaymentApprovalAsync(org, 75_000m, "DGA2");

        var sender = await RunDigestAsync();

        sender.Sent.Should().ContainSingle("the point of a digest is one message, not one per request");

        var message = sender.Sent[0];
        message.To.Should().ContainSingle().Which.Should().Be(DmdMailbox);
        message.Subject.Should().Contain("2 payments");

        // Both amounts and the total, so he can see the size of what he is
        // being asked to release without opening anything.
        message.HtmlBody.Should().Contain("40,000.00").And.Contain("75,000.00").And.Contain("115,000.00");

        // Says plainly that nothing else releases the money. The control
        // depends on this not reading as one approval among several.
        message.HtmlBody.Should().Contain("nobody else can release this money");
    }

    /// <summary>
    /// Silence when there is nothing to report, deliberately. A daily mail that
    /// usually says "nothing to do" teaches its reader to leave it unopened,
    /// and then the morning it matters looks like every other morning.
    /// </summary>
    [Fact]
    public async Task Nothing_is_sent_when_no_payment_is_waiting()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DIGEST-B"));

        // A claim that exists but has not reached him: it is still with the
        // line manager, so it is not his queue and must not appear.
        var beneficiary = await WithDbAsync(db => TestData.CreateEmployeeBeneficiaryAsync(db, org.Requester));
        await WorkflowSteps.CreateAndSubmitExpenseAsync(
            Fixture, org, beneficiary.Id, "Yes",
            TestData.ExpenseLine("Not his yet", new DateOnly(2026, 8, 9), 10_000m));

        var sender = await RunDigestAsync();

        sender.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// A role with no configured mailbox must not send, and must not pretend
    /// it did. The sweep logs a warning naming the setting; what this pins is
    /// that nothing goes out to nowhere.
    /// </summary>
    [Fact]
    public async Task Nothing_is_sent_when_the_role_has_no_mailbox()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DIGEST-C"));
        await ClaimAwaitingPaymentApprovalAsync(org, 50_000m, "DGC1");

        var sender = await RunDigestAsync(mailbox: null);

        sender.Sent.Should().BeEmpty("an unconfigured mailbox must fail loudly in the log, not quietly in the inbox");
    }

    /// <summary>
    /// Oldest first, so the thing that has been waiting longest is the first
    /// thing read. Sorting does that without any highlighting the reader has to
    /// interpret.
    /// </summary>
    [Fact]
    public async Task The_longest_waiting_request_is_listed_first()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "DIGEST-D"));

        var older = await ClaimAwaitingPaymentApprovalAsync(org, 10_000m, "DGD1");
        var newer = await ClaimAwaitingPaymentApprovalAsync(org, 20_000m, "DGD2");

        // Backdate the first so the two are unambiguously ordered. Advancing
        // the shared FakeTimeProvider is not an option -- it is assembly-wide
        // and refuses to move backwards -- so the row is aged instead, the same
        // approach ExpenseWorkflowTests uses for SLA.
        await WithDbAsync(async db =>
        {
            var request = await db.Requests.FindAsync(older);
            request!.StateEnteredAt = Fixture.TimeProvider.GetUtcNow().AddDays(-6);
            await db.SaveChangesAsync();
        });

        var sender = await RunDigestAsync();

        var body = sender.Sent.Should().ContainSingle().Subject.HtmlBody;

        var olderNumber = await WithDbAsync(async db =>
            (await db.Requests.FindAsync(older))!.RequestNumber);
        var newerNumber = await WithDbAsync(async db =>
            (await db.Requests.FindAsync(newer))!.RequestNumber);

        body.IndexOf(olderNumber, StringComparison.Ordinal).Should()
            .BeLessThan(body.IndexOf(newerNumber, StringComparison.Ordinal),
                "the request that has waited longest is the one he most needs to see");

        body.Should().Contain("6 days");
    }

    private sealed class RecordingNotificationSender : INotificationSender
    {
        public List<NotificationMessage> Sent { get; } = [];

        public string Name => "recording (test)";

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
