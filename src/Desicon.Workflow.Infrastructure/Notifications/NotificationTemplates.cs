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
}

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
