using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Functions.Infrastructure;
using Desicon.Workflow.Infrastructure.Notifications;
using Desicon.Workflow.Infrastructure.Persistence;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Functions;

/// <summary>
/// Weekday-morning summary of everything waiting on the Director of Finance.
///
/// He is the single gate on every payment Desicon makes, and gets a great deal
/// of mail. One message per request competes with all of it; one message
/// listing the queue, oldest first, with a total, does not.
///
/// NOT THROUGH THE OUTBOX, DELIBERATELY
/// ------------------------------------
/// OutboxMessage keys to one RequestId, because it exists to guarantee that a
/// notification caused by a state change commits with that change. A digest is
/// neither: it spans many requests and is caused by the clock, not by a
/// transition.
///
/// It is also self-healing in a way transactional notifications are not. If a
/// send fails, tomorrow's digest contains everything today's would have —
/// nothing is lost by not retrying, because the next run recomputes the queue
/// from scratch. Forcing it through the outbox would mean a nullable RequestId
/// and a retry mechanism for a message that is already superseded by the time
/// anyone could act on the retry.
///
/// SILENCE WHEN THERE IS NOTHING WAITING
/// -------------------------------------
/// An empty digest is not sent. A daily mail that usually says "nothing to do"
/// teaches its reader to leave it unopened, and the one morning it matters is
/// the morning it looks like all the others.
/// </summary>
internal sealed partial class PaymentApprovalDigestSweep
{
    private const string LockResource = "Desicon.Workflow.PaymentApprovalDigestSweep";

    /// <summary>
    /// The state a request sits in while it waits for him. Both modules use the
    /// same key; if a third module ever introduces its own, this is the line
    /// that needs to know about it.
    /// </summary>
    private const string AwaitingPaymentApproval = "DMD_APPROVAL";

    /// <summary>
    /// Which role's mailbox receives it. Resolved through
    /// NotificationOptions.RoleMailboxes, the same configuration the workflow
    /// definitions' role recipients use, so the digest cannot end up addressed
    /// differently from the per-request mail about the same queue.
    /// </summary>
    private const string RecipientRole = "DirectorOfFinance";

    private readonly WorkflowDbContext _db;
    private readonly IWorkflowClock _clock;
    private readonly NotificationOptions _options;
    private readonly NotificationRenderer _renderer;
    private readonly INotificationSender _sender;
    private readonly ILogger<PaymentApprovalDigestSweep> _logger;

    public PaymentApprovalDigestSweep(
        WorkflowDbContext db,
        IWorkflowClock clock,
        NotificationOptions options,
        NotificationRenderer renderer,
        INotificationSender sender,
        ILogger<PaymentApprovalDigestSweep> logger)
    {
        _db = db;
        _clock = clock;
        _options = options;
        _renderer = renderer;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>
    /// 06:00 UTC, Monday to Friday — 07:00 in Lagos, before the working day.
    /// </summary>
    /// <remarks>
    /// Weekdays only. A Saturday digest would be read on Monday alongside
    /// Monday's, and two copies of the same list is how a digest starts being
    /// skimmed. Anything raised over a weekend appears in Monday's, one day
    /// older, which is the correct emphasis.
    /// </remarks>
    [Function(nameof(PaymentApprovalDigestSweep))]
    public async Task RunAsync(
        [TimerTrigger("0 0 6 * * 1-5")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;

        await using var applicationLock = await SqlApplicationLock.TryAcquireAsync(
            _db, LockResource, TimeSpan.Zero, cancellationToken);

        if (!applicationLock.Acquired)
        {
            LogSkipped(now, LockResource);
            return;
        }

        if (!_options.RoleMailboxes.TryGetValue(RecipientRole, out var mailbox) ||
            string.IsNullOrWhiteSpace(mailbox))
        {
            // Named and logged rather than silently skipped. A digest that
            // stops arriving because a configuration entry went missing looks
            // exactly like a digest that has nothing to report.
            LogNoMailbox(RecipientRole);
            return;
        }

        var waiting = await _db.Requests
            .AsNoTracking()
            .Where(r => r.ClosedAt == null && r.CurrentState == AwaitingPaymentApproval)
            .Join(
                _db.Employees.AsNoTracking(),
                r => r.RequesterId,
                e => e.Id,
                (r, e) => new
                {
                    r.RequestId,
                    r.RequestNumber,
                    r.ModuleKey,
                    RequesterName = e.FullName,
                    r.TotalAmountNgn,
                    r.StateEnteredAt
                })
            .OrderBy(x => x.StateEnteredAt)
            .ToListAsync(cancellationToken);

        if (waiting.Count == 0)
        {
            LogNothingWaiting(now);
            return;
        }

        var items = waiting
            .Select(x => new DigestItem(
                x.RequestId,
                x.RequestNumber,
                x.ModuleKey,
                x.RequesterName,
                x.TotalAmountNgn,
                // Calendar days, not working days. He is being told how long
                // somebody has been waiting for their money, and the person
                // waiting counts weekends.
                WaitingDays: Math.Max(0, (int)(now - x.StateEnteredAt).TotalDays)))
            .ToList();

        var (subject, body) = _renderer.RenderPaymentApprovalDigest(items);

        await _sender.SendAsync(
            new NotificationMessage(
                To: new[] { mailbox },
                Subject: subject,
                HtmlBody: body,
                // No single request number applies. The oldest is the one the
                // digest exists to surface, so it is the one worth carrying
                // into whatever the transport logs.
                RequestNumber: items[0].RequestNumber),
            cancellationToken);

        LogSent(items.Count, items[0].WaitingDays, mailbox);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment approval digest skipped at {Now}: another instance holds {Resource}.")]
    private partial void LogSkipped(DateTimeOffset now, string resource);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Payment approval digest not sent: no mailbox is configured for role {Role}. " +
                  "Set Notifications__RoleMailboxes__{Role}.")]
    private partial void LogNoMailbox(string role);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment approval digest not sent at {Now}: nothing is awaiting payment approval.")]
    private partial void LogNothingWaiting(DateTimeOffset now);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Payment approval digest sent to {Mailbox}: {Count} request(s), oldest waiting {OldestDays} day(s).")]
    private partial void LogSent(int count, int oldestDays, string mailbox);
}
