namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// A file attached to a request: a receipt, an invoice, a delivery note.
/// </summary>
/// <remarks>
/// The evidence half of a retirement. Until this existed, a claim asserted
/// <c>ReceiptStatus = Yes</c> and nothing stood behind it — the Accounts
/// Officer posting it in Business Central had a tick box and no way to see
/// what was actually bought.
///
/// Metadata lives here; bytes live in the immutable blob container the
/// infrastructure has provisioned since day one and which nothing had ever
/// written to. The split matters: the container carries a seven-year WORM
/// retention policy, so a row here can be deleted and the file it names
/// cannot. Deliberate — a receipt that can disappear is not evidence.
/// </remarks>
public sealed class Attachment
{
    public Guid AttachmentId { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    /// <summary>The name as the uploader saw it. Display only.</summary>
    /// <remarks>
    /// Never used to build the blob path. A filename is user input and arrives
    /// with directory separators, leading dots and reserved names in it; the
    /// path is built from AttachmentId instead, so nothing a person types can
    /// reach the storage layout.
    /// </remarks>
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Where the bytes are, relative to the container.</summary>
    public string BlobPath { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the uploaded bytes, hex, lower case.
    /// </summary>
    /// <remarks>
    /// Recorded so a file produced in an audit can be shown to be the file
    /// that was uploaded. The same reasoning as the hash chain over
    /// AuditEvent: an unverifiable record of a payment is not much better than
    /// no record, and "the receipt was swapped afterwards" is a claim the
    /// platform should be able to answer rather than debate.
    /// </remarks>
    public string Sha256 { get; set; } = string.Empty;

    public Guid UploadedByUserId { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}
