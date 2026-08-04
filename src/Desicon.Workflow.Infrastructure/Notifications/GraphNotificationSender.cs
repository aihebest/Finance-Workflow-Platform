using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Desicon.Workflow.Infrastructure.Notifications;

/// <summary>
/// Sends via Microsoft Graph, as the platform's own identity, from a shared
/// mailbox.
///
/// WHY RAW HTTP RATHER THAN THE GRAPH SDK
/// --------------------------------------
/// sendMail is one POST with a small JSON body. The Graph SDK would bring a
/// large transitive dependency graph into a Function App that needs exactly
/// one operation from it, and every one of those packages is surface for the
/// Trivy and dependency scans to report on. The trade would be worth it for
/// broad Graph use; it is not for this.
///
/// PERMISSION SCOPE IS THE RISK HERE
/// ---------------------------------
/// Mail.Send as an *application* permission lets the caller send as anybody
/// in the tenant. It must be constrained by an Exchange application access
/// policy limited to the single shared mailbox, or this platform can email
/// as the Managing Director. That policy is Exchange configuration, not
/// code, so nothing in this repository can enforce or verify it — which is
/// precisely why it is written here as well as in the build plan.
///
/// The credential is a TokenCredential, so in Azure this is the Function
/// App's managed identity and no secret exists. Locally it falls back to the
/// developer's az login.
/// </summary>
public sealed partial class GraphNotificationSender : INotificationSender
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly NotificationOptions _options;
    private readonly ILogger<GraphNotificationSender> _logger;

    public GraphNotificationSender(
        HttpClient http,
        TokenCredential credential,
        NotificationOptions options,
        ILogger<GraphNotificationSender> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SenderMailbox))
        {
            throw new InvalidOperationException(
                "Notifications:SenderMailbox is not configured. The Graph sender cannot send from an unspecified mailbox.");
        }
    }

    public string Name => $"Microsoft Graph ({_options.SenderMailbox})";

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(GraphScopes), cancellationToken);

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(_options.SenderMailbox)}/sendMail")
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    subject = message.Subject,
                    body = new { contentType = "HTML", content = message.HtmlBody },

                    // toRecipients rather than bcc: these are colleagues on a
                    // shared approval, and hiding who else was asked makes it
                    // impossible to tell whether the right people were told.
                    toRecipients = message.To
                        .Select(address => new { emailAddress = new { address } })
                        .ToArray()
                },

                // The shared mailbox is a record of what the platform sent.
                // Without this the Sent Items folder stays empty and the only
                // evidence a notification went out is this application's own
                // logs, which is the wrong place to keep it.
                saveToSentItems = true
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Graph's error body carries the actual cause — a missing
            // application access policy reads as ErrorAccessDenied, which is
            // indistinguishable from a missing consent grant without it.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);

            LogSendFailed(message.RequestNumber, (int)response.StatusCode, Truncate(detail, 500));

            throw new HttpRequestException(
                $"Graph sendMail failed with {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 500)}");
        }

        LogSent(message.RequestNumber, message.To.Count);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    [LoggerMessage(
        EventId = 7101,
        Level = LogLevel.Information,
        Message = "Sent notification for {RequestNumber} to {RecipientCount} recipient(s) via Graph.")]
    private partial void LogSent(string requestNumber, int recipientCount);

    [LoggerMessage(
        EventId = 7102,
        Level = LogLevel.Error,
        Message = "Graph sendMail failed for {RequestNumber}: HTTP {StatusCode}. {Detail}")]
    private partial void LogSendFailed(string requestNumber, int statusCode, string detail);
}
