using Azure.Core;
using Desicon.Workflow.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Desicon.Workflow.Infrastructure.DependencyInjection;

/// <summary>
/// Wires the outbox dispatcher and its transport.
/// </summary>
public static class NotificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers notification services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Base URL and sender mailbox.</param>
    /// <param name="credential">
    /// Token credential for Graph. In Azure this is the Function App's
    /// managed identity; locally it falls back to the developer's az login.
    /// Ignored when the logging sender is selected.
    /// </param>
    /// <param name="useGraph">
    /// True to send via Microsoft Graph, false to log instead.
    ///
    /// Passed explicitly rather than inferred from whether SenderMailbox is
    /// set. Inference would mean a deployment that lost its configuration
    /// silently downgraded to sending nothing while reporting success —
    /// which is the exact failure mode this codebase keeps finding, and the
    /// one a notification system can least afford.
    /// </param>
    public static IServiceCollection AddNotifications(
        this IServiceCollection services,
        NotificationOptions options,
        TokenCredential credential,
        bool useGraph)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (useGraph && string.IsNullOrWhiteSpace(options.SenderMailbox))
        {
            throw new InvalidOperationException(
                "Graph notifications are enabled but Notifications:SenderMailbox is not set. " +
                "Set the shared mailbox, or set Notifications:UseGraph to false to log instead of sending.");
        }

        services.AddSingleton(options);
        services.AddScoped<NotificationRecipientResolver>();
        services.AddSingleton(_ => new NotificationRenderer(options));
        services.AddScoped<OutboxDispatcher>();

        if (useGraph)
        {
            ArgumentNullException.ThrowIfNull(credential);

            services.AddSingleton(credential);
            services.AddHttpClient<INotificationSender, GraphNotificationSender>();
        }
        else
        {
            services.AddSingleton<INotificationSender, LoggingNotificationSender>();
        }

        return services;
    }
}
