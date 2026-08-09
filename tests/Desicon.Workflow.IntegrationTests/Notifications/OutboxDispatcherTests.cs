using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Domain.Notifications;
using Desicon.Workflow.Infrastructure.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.Notifications;

/// <summary>
/// The outbox has had producers since step 2 and no consumer until now.
/// These tests cover the three outcomes that matter operationally: a message
/// is sent exactly once, a message that can never be delivered is parked with
/// a reason rather than retried forever, and a transport that keeps failing
/// eventually stops being retried.
///
/// The last two are the ones worth having. A dispatcher that only ever gets
/// tested on the happy path will retry an undeliverable message until someone
/// notices the log volume.
/// </summary>
public sealed class OutboxDispatcherTests : IntegrationTestBase
{
    public OutboxDispatcherTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task A_pending_message_is_sent_once_to_the_resolved_recipient()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-A"));

        // Submitting writes a STATE_ENTERED notification to CurrentActor,
        // which resolves to the line manager. Using the real producer rather
        // than hand-inserting a row keeps this honest about the shape the
        // dispatcher actually receives.
        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Notify me", 1_000m);

        var sender = new RecordingNotificationSender();

        var dispatched = await DispatchAsync(sender);

        dispatched.Should().BeGreaterThan(0);
        sender.Sent.Should().NotBeEmpty();

        var message = sender.Sent[0];
        message.To.Should().Contain(org.LineManager.Email);
        message.Subject.Should().Contain("cash advance");
        message.HtmlBody.Should().Contain(requestId.ToString(), "the deep link addresses the specific request");

        var rows = await WithDbAsync(async db => await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.RequestId == requestId)
            .ToListAsync());

        rows.Should().OnlyContain(m => m.Status == OutboxMessageStatus.Dispatched);
        rows.Should().OnlyContain(m => m.DispatchedAt != null);

        // A second run must not resend: Dispatched rows are not picked up.
        var second = await DispatchAsync(sender);
        second.Should().Be(0);
    }

    /// <summary>
    /// A role specifier with no mailbox configured for it.
    ///
    /// Roles reach this application only as a claim on an incoming token, so
    /// there is no membership store to resolve one against. Version 2 added
    /// NotificationOptions.RoleMailboxes, which answers the question for any
    /// role an administrator has configured -- but a role with no entry is
    /// still unresolvable, and that is what this covers. DispatchAsync
    /// deliberately builds options with no RoleMailboxes at all so the case
    /// stays reachable.
    ///
    /// The dispatcher must name the specifier rather than send to nobody and
    /// report success, and must not burn five retry attempts on something no
    /// amount of waiting will fix. A finance approval nobody was told about is
    /// the failure this system exists to prevent, so silence is the one answer
    /// it must never give.
    /// </summary>
    [Fact]
    public async Task A_message_with_no_resolvable_recipient_is_parked_with_the_specifier_named()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-B"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Unresolvable recipient", 1_000m);

        // Submission queues its own legitimate action-required message.
        // Drain it first so what follows is about the unresolvable row only.
        await DispatchAsync(new RecordingNotificationSender());

        await WithDbAsync(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                RequestId = requestId,
                Template = "action-required",
                RecipientRolesJson = """["FinanceManager"]""",
                PayloadJson = """{"RequestNumber":"ADV-TEST","ModuleKey":"CASH_ADVANCE"}""",
                Status = OutboxMessageStatus.Pending,
                CreatedAt = Fixture.TimeProvider.GetUtcNow()
            });

            await db.SaveChangesAsync();
        });

        var sender = new RecordingNotificationSender();

        await DispatchAsync(sender);

        var parked = await WithDbAsync(async db => await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.RequestId == requestId && m.RecipientRolesJson.Contains("FinanceManager"))
            .SingleAsync());

        parked.Status.Should().Be(OutboxMessageStatus.Failed);
        parked.LastError.Should().Contain("FinanceManager",
            "naming the specifier points at the missing role store, not at the mail transport");
        parked.AttemptCount.Should().Be(1, "an unresolvable recipient is not a transient failure");

        sender.Sent.Should().BeEmpty("nothing should be sent when there is no one to send it to");
    }

    /// <summary>
    /// A transport that keeps failing must stop being retried. Five attempts
    /// gets through a Graph outage or a throttling window; past that the
    /// cause is almost always permanent and retrying hides it.
    /// </summary>
    [Fact]
    public async Task A_failing_transport_is_retried_then_parked()
    {
        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "OBX-C"));

        var requestId = await WorkflowSteps.CreateAndSubmitCashAdvanceAsync(
            Fixture, org, "Transport keeps failing", 1_000m);

        var sender = new FailingNotificationSender("Graph returned 503.");

        // Each pass processes the message once, as five separate ticks would.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await DispatchAsync(sender);
        }

        var rows = await WithDbAsync(async db => await db.OutboxMessages
            .AsNoTracking()
            .Where(m => m.RequestId == requestId)
            .ToListAsync());

        rows.Should().OnlyContain(m => m.Status == OutboxMessageStatus.Failed);
        rows.Should().OnlyContain(m => m.AttemptCount >= 5);
        rows.Should().OnlyContain(m => m.LastError!.Contains("503"));

        // Parked messages are not picked up again, so the log does not fill
        // with a failure nobody has acted on.
        var attemptsBefore = rows[0].AttemptCount;
        await DispatchAsync(sender);

        var after = await WithDbAsync(async db => await db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.OutboxMessageId == rows[0].OutboxMessageId));

        after.AttemptCount.Should().Be(attemptsBefore);
    }

    /// <summary>
    /// Builds the dispatcher from the API host's container plus the two
    /// notification services constructed here.
    ///
    /// The API deliberately does not register notification services: it
    /// produces outbox rows and the Functions host consumes them, so wiring a
    /// dispatcher into the API would register a component nothing there uses
    /// and imply the API sends mail. The database context and actor resolver
    /// do come from the container, because those must be the same ones the
    /// application uses — resolving recipients through a different code path
    /// from the one that resolves authority is exactly the drift this design
    /// avoids.
    /// </summary>
    private async Task<int> DispatchAsync(INotificationSender sender)
    {
        using var scope = Fixture.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<WorkflowDbContext>();

        // Built here rather than resolved: WorkflowApiFactory does not call
        // AddNotifications, so nothing registers NotificationOptions in the
        // test container. Constructing it locally also keeps the role mailboxes
        // under this test's control -- see
        // A_message_with_no_resolvable_recipient_is_parked_with_the_specifier_named,
        // which depends on a role having NO mailbox configured.
        var options = new NotificationOptions
        {
            ApplicationBaseUrl = "https://finance.desicon.test",
            SenderMailbox = "finance-workflow@desicon.test"
        };

        var dispatcher = new OutboxDispatcher(
            db,
            sender,
            new NotificationRecipientResolver(db, sp.GetRequiredService<IActorResolver>(), options),
            new NotificationRenderer(options),
            sp.GetRequiredService<IWorkflowClock>());

        return await dispatcher.DispatchPendingAsync(cancellationToken: CancellationToken.None);
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

    private sealed class FailingNotificationSender : INotificationSender
    {
        private readonly string _error;

        public FailingNotificationSender(string error) => _error = error;

        public string Name => "always fails (test)";

        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException(_error);
    }
}
