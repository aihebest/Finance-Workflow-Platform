using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Desicon.Workflow.Infrastructure.Attachments;

/// <summary>
/// Options for the attachments container.
/// </summary>
/// <param name="BlobEndpoint">
/// e.g. https://stdesiconfwdev.blob.core.windows.net. Empty disables
/// attachments rather than failing at startup — see AddAttachments.
/// </param>
/// <param name="ContainerName">The container. Provisioned as "attachments".</param>
public sealed record AttachmentStorageOptions(string BlobEndpoint, string ContainerName);

/// <summary>
/// Writes attachment bytes to the immutable blob container.
/// </summary>
/// <remarks>
/// The container, its customer-managed key, its private endpoint and the
/// App Service's "Storage Blob Data Contributor" role assignment have all
/// existed since the infrastructure was written. Nothing had ever written a
/// byte to it — the app was never given the endpoint, and no code asked for
/// one. This is the first thing to use any of it.
///
/// Authenticates with the app's managed identity via DefaultAzureCredential,
/// the same way Key Vault and SQL do. No connection string, no account key:
/// a key in configuration is a credential that outlives the person who put it
/// there.
/// </remarks>
public sealed class BlobAttachmentStore : IAttachmentStore
{
    private readonly BlobContainerClient _container;

    public BlobAttachmentStore(BlobServiceClient serviceClient, AttachmentStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(serviceClient);
        ArgumentNullException.ThrowIfNull(options);

        _container = serviceClient.GetBlobContainerClient(options.ContainerName);
    }

    public async Task<StoredBlob> SaveAsync(
        Guid requestId,
        Guid attachmentId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Path from ids alone. The uploaded filename is display metadata and
        // never touches this: it arrives containing "../", drive letters and
        // reserved device names, and the only reliable defence is not to use
        // it. The original name is kept in Attachment.FileName.
        var path = $"{requestId:D}/{attachmentId:D}";

        // Hash while uploading rather than buffering the file to hash it
        // first. A receipt is small, but "read it all into memory to check it"
        // is how an upload endpoint becomes a way to exhaust a web server.
        using var sha = SHA256.Create();
        await using var hashing = new CryptoStream(content, sha, CryptoStreamMode.Read, leaveOpen: true);

        var blob = _container.GetBlobClient(path);

        await blob.UploadAsync(
            hashing,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType,

                    // Attachment, always. A receipt is opened by an approver in
                    // a browser, and an inline text/html or SVG served from the
                    // application's own origin would run as the application.
                    ContentDisposition = "attachment"
                }
            },
            cancellationToken);

        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);

        return new StoredBlob(
            path,
            properties.Value.ContentLength,
            Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    public async Task<Stream> OpenAsync(string blobPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobPath);

        var blob = _container.GetBlobClient(blobPath);
        return await blob.OpenReadAsync(cancellationToken: cancellationToken);
    }
}
