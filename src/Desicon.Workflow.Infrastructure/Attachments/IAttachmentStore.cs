namespace Desicon.Workflow.Infrastructure.Attachments;

/// <summary>Where an uploaded file went, and what it hashed to.</summary>
/// <param name="BlobPath">Relative to the container.</param>
/// <param name="SizeBytes">Bytes actually written, not what the client claimed.</param>
/// <param name="Sha256">Hex, lower case, computed while streaming.</param>
public sealed record StoredBlob(string BlobPath, long SizeBytes, string Sha256);

/// <summary>
/// Reads and writes attachment bytes.
/// </summary>
/// <remarks>
/// An interface with one real implementation, for one reason worth stating:
/// the integration suite runs against Testcontainers with no Azure Storage,
/// and the alternative to this seam is either a test that talks to real
/// storage or no attachment tests at all. Both are worse.
///
/// Deliberately narrow. Deleting is absent because the container carries a
/// seven-year WORM retention policy — a delete would fail at the storage
/// layer, and an interface offering an operation that always throws teaches
/// people the wrong thing about what the system can do.
/// </remarks>
public interface IAttachmentStore
{
    /// <summary>Streams <paramref name="content"/> into the container.</summary>
    /// <remarks>
    /// The path is derived from the ids, never from the uploaded filename: a
    /// filename is user input and arrives containing directory separators and
    /// reserved names.
    /// </remarks>
    Task<StoredBlob> SaveAsync(
        Guid requestId,
        Guid attachmentId,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a blob for reading. The caller disposes the stream.</summary>
    Task<Stream> OpenAsync(string blobPath, CancellationToken cancellationToken = default);
}
