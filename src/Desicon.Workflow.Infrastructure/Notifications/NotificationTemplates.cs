using System.Net;
using System.Text.Json;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// Options controlling how notifications are addressed and linked.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    /// Base URL of the application, used to build the deep link. No trailing
    /// slash.
    /// </summary>
    public string ApplicationBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared mailbox the platform sends as. Empty when no mailbox has been
    /// provisioned yet, which is why LoggingNotificationSender exists.
    /// </summary>
    public string SenderMailbox { get; set; } = string.Empty;

    /// <summary>
    /// Where to write when a workflow definition names a role rather than a
    /// person: role key (CostControlOfficer, TreasuryOfficer, FinanceManager,
    /// DirectorOfFinance) to
    /// mailbox address.
    /// </summary>
    /// <remarks>
    /// Roles live in Entra as app role assignments and reach this application
    /// only as a claim on an incoming token. Nothing records who holds one, so
    /// "email the Accounts Officer" had no list to read and every role-based
    /// notification resolved to nobody -- which was every step in the Finance
    /// chain.
    ///
    /// A shared mailbox per role rather than a lookup of role holders, and the
    /// reason is operational rather than technical: the request that goes
    /// missing is the one that arrived while its owner was on leave. A mailbox
    /// the team watches survives an absence; an individual's inbox does not.
    /// It also keeps this out of Graph, so no extra application permission is
    /// needed to send a notification.
    ///
    /// Configured as Notifications:RoleMailboxes:TreasuryOfficer and so on. A
    /// role with no entry stays unresolved and is reported by name, exactly as
    /// before -- silence is never the answer here.
    /// </remarks>
    public Dictionary<string, string> RoleMailboxes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One line of a digest.
/// </summary>
/// <param name="RequestId">For the deep link.</param>
/// <param name="RequestNumber">What the reader will quote when they ask about it.</param>
/// <param name="ModuleKey">EXPENSE or CASH_ADVANCE, rendered in words.</param>
/// <param name="RequesterName">Whose request it is. A digest without names is a list of numbers.</param>
/// <param name="AmountNgn">Naira. The digest totals these.</param>
/// <param name="WaitingDays">Calendar days in the current state, for sorting and for shame.</param>
public sealed record DigestItem(
    Guid RequestId,
    string RequestNumber,
    string ModuleKey,
    string RequesterName,
    decimal AmountNgn,
    int WaitingDays);

/// <summary>
/// Renders the templates the workflow definitions reference.
///
/// Copy lives in code rather than in files, deliberately and provisionally.
/// There are eleven short templates, no CMS, and nobody outside engineering
/// editing them today; a file-based renderer would add path plumbing to two
/// hosts and a second thing to keep in the deployment package for no present
/// benefit. When Finance or Comms want to own the wording, this becomes
/// files — and the seam to change is this class alone.
///
/// Every template carries the deep link. Per the step 7 decision, that link
/// is a plain URL: the approver signs in with Entra and acts in the
/// application. An action token that authorises a state change from a
/// mailbox would be a bearer credential sitting in an inbox and a mail
/// relay, forwardable and outside maker-checker, and deserves its own design
/// rather than arriving alongside a dispatcher.
/// </summary>
public sealed class NotificationRenderer
{
    private readonly NotificationOptions _options;

    public NotificationRenderer(NotificationOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Renders subject and body for a template, given the outbox payload.
    /// An unknown template renders a generic body rather than throwing: the
    /// alternative is that adding a template to a workflow definition and
    /// forgetting to add it here silently poisons every affected message.
    /// </summary>
    public (string Subject, string HtmlBody) Render(string template, JsonElement payload)
    {
        var requestNumber = ReadString(payload, "RequestNumber") ?? "(unknown)";
        var moduleKey = ReadString(payload, "ModuleKey") ?? string.Empty;
        var requestId = ReadString(payload, "RequestId") ?? string.Empty;
        var toState = ReadString(payload, "ToState");
        var currentState = ReadString(payload, "CurrentState") ?? toState;

        var link = BuildDeepLink(requestId);
        var module = FriendlyModule(moduleKey);

        var (subject, lead) = template switch
        {
            "action-required" =>
                ($"Action required: {module} {requestNumber}",
                 "A request is waiting for you. It will not move until you act on it."),

            "reminder" =>
                ($"Reminder: {module} {requestNumber} is still waiting",
                 "This request is still waiting for you and its deadline has not yet passed."),

            // The Director of Finance's gate. Worded to say plainly that
            // nothing else releases the money, because the whole control
            // depends on it not being mistaken for one approval among several.
            "payment-approval-required" =>
                ($"Payment approval required: {module} {requestNumber}",
                 "This request has been approved by Accounts and is waiting for your authorisation to pay. No payment can be made against it until you approve — nobody else can release this money."),

            // The Accounts Officer's queue. Names Business Central explicitly:
            // the action is not in this application, and an email that does
            // not say so sends her looking for a button that does not exist.
            "posting-required" =>
                ($"Ready to post: {module} {requestNumber}",
                 "This request is fully approved and is waiting to be posted in Business Central. Post it there, then come back and record the BC document number against it."),

            "escalation" =>
                ($"Escalated: {module} {requestNumber}",
                 "This request passed its service level without being actioned, so authority has moved to the next approver. It is now theirs to action."),

            "returned-for-correction" =>
                ($"Returned for correction: {module} {requestNumber}",
                 "Your request has been returned. Correct it and resubmit — it keeps its number and its history."),

            "rejected" =>
                ($"Rejected: {module} {requestNumber}",
                 "Your request has been rejected. The reason is recorded against it."),

            "closed" =>
                ($"Closed: {module} {requestNumber}",
                 "This request is now closed. No further action is needed."),

            "confirm-receipt" =>
                ($"Please confirm receipt: {module} {requestNumber}",
                 "Payment has been made against this request. Please confirm you received it — the request stays open until you do."),

            "confirm-cash-receipt" =>
                ($"Please confirm cash receipt: {module} {requestNumber}",
                 "Cash has been released to you. Please acknowledge it. The retirement clock has already started."),

            "retirement-due-soon" =>
                ($"Retirement due soon: {module} {requestNumber}",
                 "This cash advance is due for retirement shortly. Submit your expense claim against it before the deadline."),

            "retirement-due" =>
                ($"Retirement due: {module} {requestNumber}",
                 "This cash advance is due for retirement now."),

            // The one branch where money comes back TO Desicon, and until
            // 17 Aug 2026 the only step in either module with no notification
            // at all. A retirement showing the employee spent less than the
            // advance moved to REFUND_DUE and simply sat there: the employee
            // was never told they owed money, and the Accounts Manager was
            // never told to expect it.
            //
            // Two templates rather than one, because the two readers need
            // different things. Sending one message to both would have to be
            // vague enough to suit either, and a vague message about money
            // owed is one nobody acts on.
            "refund-due" =>
                ($"Refund due from you: {module} {requestNumber}",
                 "Your retirement shows you spent less than the advance you took, so the balance is owed back to Desicon. Pay it in, then Accounts will confirm receipt and close this. Until that happens the advance stays outstanding against you and you cannot take another."),

            "refund-confirmation-required" =>
                ($"Refund to confirm: {module} {requestNumber}",
                 "This retirement shows the employee spent less than the advance and owes the balance back. Once the money is received, confirm it here — the request cannot close until you do."),

            "retirement-overdue" =>
                ($"Overdue retirement: {module} {requestNumber}",
                 "This cash advance is past its retirement deadline. Until it is retired you cannot raise another advance."),

            _ =>
                ($"Update: {module} {requestNumber}",
                 "There has been an update to this request.")
        };

        var body = BuildBody(lead, requestNumber, module, currentState, link);

        return (subject, body);
    }

    /// <summary>
    /// One email listing everything waiting on a person, rather than one email
    /// per item.
    /// </summary>
    /// <param name="items">
    /// Oldest first. The point of a digest is to make the thing that has been
    /// waiting longest impossible to miss, and sorting by age does that
    /// without any highlighting the reader has to interpret.
    /// </param>
    /// <remarks>
    /// Separate from <see cref="Render"/> because a digest is not a template
    /// applied to one request: it has no single request number, no single
    /// state, and its deep link is per row. Forcing it through the same
    /// signature would mean inventing a payload shape that pretends otherwise.
    /// </remarks>
    public (string Subject, string HtmlBody) RenderPaymentApprovalDigest(
        IReadOnlyList<DigestItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var subject = items.Count == 1
            ? "1 payment awaiting your approval"
            : $"{items.Count} payments awaiting your approval";

        var rows = string.Concat(items.Select(item =>
        {
            var link = BuildDeepLink(item.RequestId.ToString());
            var number = string.IsNullOrWhiteSpace(link)
                ? WebUtility.HtmlEncode(item.RequestNumber)
                : $"""<a href="{link}">{WebUtility.HtmlEncode(item.RequestNumber)}</a>""";

            var waited = item.WaitingDays == 1 ? "1 day" : $"{item.WaitingDays} days";

            return $"""
                <tr>
                  <td style="padding:6px 12px 6px 0">{number}</td>
                  <td style="padding:6px 12px 6px 0">{WebUtility.HtmlEncode(FriendlyModule(item.ModuleKey))}</td>
                  <td style="padding:6px 12px 6px 0">{WebUtility.HtmlEncode(item.RequesterName)}</td>
                  <td style="padding:6px 12px 6px 0;text-align:right">{item.AmountNgn:N2}</td>
                  <td style="padding:6px 0">{waited}</td>
                </tr>
                """;
        }));

        var total = items.Sum(i => i.AmountNgn);

        // States plainly that nothing else releases the money. The whole
        // control depends on this not reading as one approval among several,
        // and a digest is read faster than a single-request mail.
        var body = $"""
            <p>The following {(items.Count == 1 ? "payment is" : "payments are")} waiting for your approval.
            No payment can be made against any of them until you approve — nobody else can release this money.</p>
            <table style="border-collapse:collapse;font-family:sans-serif;font-size:14px">
              <thead>
                <tr style="text-align:left;border-bottom:1px solid #ccc">
                  <th style="padding:6px 12px 6px 0">Request</th>
                  <th style="padding:6px 12px 6px 0">Type</th>
                  <th style="padding:6px 12px 6px 0">Raised by</th>
                  <th style="padding:6px 12px 6px 0;text-align:right">Amount (NGN)</th>
                  <th style="padding:6px 0">Waiting</th>
                </tr>
              </thead>
              <tbody>{rows}</tbody>
              <tfoot>
                <tr style="border-top:1px solid #ccc;font-weight:600">
                  <td colspan="3" style="padding:6px 12px 6px 0">Total</td>
                  <td style="padding:6px 12px 6px 0;text-align:right">{total:N2}</td>
                  <td></td>
                </tr>
              </tfoot>
            </table>
            <p style="color:#666;font-size:12px">Oldest first. This is sent on weekday mornings and only when something is waiting.</p>
            """;

        return (subject, body);
    }

    private string BuildDeepLink(string requestId) =>
        string.IsNullOrWhiteSpace(_options.ApplicationBaseUrl) || string.IsNullOrWhiteSpace(requestId)
            ? string.Empty
            : $"{_options.ApplicationBaseUrl.TrimEnd('/')}/requests/{requestId}";

    private static string BuildBody(
        string lead, string requestNumber, string module, string? state, string link)
    {
        // Hand-built rather than templated, and every interpolated value is
        // HTML-encoded. Request numbers and state keys are system-generated,
        // but module definitions are JSON files that a future admin edits, so
        // treating them as trusted would be an assumption with no test behind
        // it — the shape of defect this project keeps finding.
        var linkBlock = string.IsNullOrEmpty(link)
            ? "<p>Open the finance workflow platform to view this request.</p>"
            : $"""<p><a href="{WebUtility.HtmlEncode(link)}">Open {WebUtility.HtmlEncode(requestNumber)}</a></p>""";

        var stateBlock = string.IsNullOrEmpty(state)
            ? string.Empty
            : $"<p><strong>Current status:</strong> {WebUtility.HtmlEncode(state)}</p>";

        return $"""
            <html>
              <body style="font-family: Segoe UI, Arial, sans-serif; font-size: 14px; color: #1f2937;">
                <p>{WebUtility.HtmlEncode(lead)}</p>
                <p><strong>Request:</strong> {WebUtility.HtmlEncode(requestNumber)} ({WebUtility.HtmlEncode(module)})</p>
                {stateBlock}
                {linkBlock}
                <hr style="border: none; border-top: 1px solid #e5e7eb; margin: 24px 0;" />
                <p style="color: #6b7280; font-size: 12px;">
                  Sent by the Desicon Finance Workflow Platform. Do not reply to this message.
                </p>
              </body>
            </html>
            """;
    }

    private static string FriendlyModule(string moduleKey) => moduleKey switch
    {
        "CASH_ADVANCE" => "cash advance",
        "EXPENSE" => "expense claim",
        "LEAVE_REQUEST" => "leave request",
        _ => "request"
    };

    private static string? ReadString(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
