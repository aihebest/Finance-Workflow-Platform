using Azure.Core;
using Azure.Storage.Blobs;
using Desicon.Workflow.Infrastructure.Attachments;
using Microsoft.Extensions.DependencyInjection;

namespace Desicon.Workflow.Infrastructure.DependencyInjection;

public static class AttachmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the attachment store, if a blob endpoint is configured.
    /// </summary>
    /// <remarks>
    /// Absent configuration disables uploads rather than failing at startup,
    /// and the endpoints then answer 503 naming the missing setting. That is
    /// the opposite of the choice made for notifications, where UseGraph must
    /// be stated explicitly — and deliberately so. A notification system that
    /// silently logs instead of sending looks like it is working; an upload
    /// that refuses tells the person standing in front of it immediately.
    ///
    /// The credential is the host's decision, as with Graph: managed identity
    /// in Azure, az login locally. The App Service already holds "Storage Blob
    /// Data Contributor" on the account -- that role assignment has existed
    /// since the infrastructure was written, waiting for something to use it.
    /// </remarks>
    public static IServiceCollection AddAttachments(
        this IServiceCollection services,
        AttachmentStorageOptions options,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BlobEndpoint))
        {
            return services;
        }

        ArgumentNullException.ThrowIfNull(credential);

        services.AddSingleton(options);
        services.AddSingleton(_ => new BlobServiceClient(new Uri(options.BlobEndpoint), credential));
        services.AddScoped<IAttachmentStore, BlobAttachmentStore>();

        return services;
    }
}
