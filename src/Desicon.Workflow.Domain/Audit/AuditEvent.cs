using System.Security.Cryptography;
using System.Text;

namespace Desicon.Workflow.Domain.Audit;

/// <summary>
/// Append-only audit event.
///
/// The application identity holds INSERT on this table and nothing else -- no
/// UPDATE, no DELETE. Each row also chains to the previous event for the same
/// request, so altering history breaks the chain and the nightly verification
/// job notices.
///
/// This is more than the brief asks for, and it is the difference between an
/// audit trail and a log. A trail the application can rewrite proves nothing.
/// </summary>
public sealed class AuditEvent
{
    public long AuditEventId { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>Submitted, Approved, Returned, Rejected, Escalated,
    /// AttachmentUploaded, Posted, Authorised, PaymentExecuted, Acknowledged,
    /// RoleAssignmentChanged, BreakGlassElevation, AuthorisationDenied.</summary>
    public string EventType { get; set; } = string.Empty;

    public string? FromState { get; set; }

    public string? ToState { get; set; }

    public Guid ActorId { get; set; }

    public string ActorRole { get; set; } = string.Empty;

    /// <summary>Populated when the actor is acting under a delegation.</summary>
    public Guid? OnBehalfOfUserId { get; set; }

    public string? Reason { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public string? ClientIpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Caller-supplied idempotency key for the action that produced
    /// this event. Null for events that were not produced by a retryable
    /// actor-initiated action (e.g. a scheduled SLA sweep). Not part of the
    /// hash chain -- it is a deduplication concern for the write path, not
    /// part of what the chain proves happened.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>EventHash of the previous event for this request. Null for the
    /// first event.</summary>
    public byte[]? PreviousHash { get; set; }

    public byte[] EventHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Computes this event's hash over its own content plus the previous
    /// event's hash. Call once, immediately before insert.
    /// </summary>
    public void Seal(byte[]? previousHash)
    {
        PreviousHash = previousHash;
        EventHash = ComputeHash(this, previousHash);
    }

    public bool VerifyAgainst(byte[]? previousHash) =>
        CryptographicOperations.FixedTimeEquals(EventHash, ComputeHash(this, previousHash));

    /// <summary>
    /// Fields covered by the hash.
    ///
    /// Reason, OnBehalfOfUserId, ClientIpAddress and CorrelationId were
    /// originally omitted, which left them silently editable: changing an
    /// approver's stated reason for a rejection altered no behaviour and
    /// broke no hash, so nothing could ever have detected it. That is the
    /// one field a person with database access would most want to revise
    /// after the fact.
    ///
    /// OnBehalfOfUserId matters at least as much. It records delegation --
    /// who acted for whom -- and EscalationSweep uses it to name the actor
    /// who failed to act. Both are claims about people, and neither was
    /// tamper-evident.
    ///
    /// IdempotencyKey is deliberately excluded: it is a de-duplication
    /// mechanism rather than a record of what happened, it is already
    /// protected by a unique index, and including it would make the hash
    /// depend on how a request arrived rather than on what it did.
    ///
    /// CHANGING THIS INVALIDATES EXISTING CHAINS. Any event sealed under the
    /// old field set fails verification, because its recorded hash was
    /// computed over less. Acceptable now, before the platform holds real
    /// approvals; not later, at which point the answer is a version marker
    /// on the event and a verifier that checks each event against the rules
    /// in force when it was sealed.
    /// </summary>
    private static byte[] ComputeHash(AuditEvent e, byte[]? previousHash)
    {
        var builder = new StringBuilder()
            .Append(e.RequestId.ToString("N"))
            .Append('\u001f')
            .Append(e.EventType)
            .Append('\u001f')
            .Append(e.FromState)
            .Append('\u001f')
            .Append(e.ToState)
            .Append('\u001f')
            .Append(e.ActorId.ToString("N"))
            .Append('\u001f')
            .Append(e.ActorRole)
            .Append('\u001f')
            .Append(e.OnBehalfOfUserId?.ToString("N"))
            .Append('\u001f')
            .Append(e.Reason)
            .Append('\u001f')
            .Append(e.ClientIpAddress)
            .Append('\u001f')
            .Append(e.CorrelationId)
            .Append('\u001f')
            .Append(e.OccurredAtUtc.UtcDateTime.ToString("O"))
            .Append('\u001f')
            .Append(e.PayloadJson);

        var content = Encoding.UTF8.GetBytes(builder.ToString());

        if (previousHash is null || previousHash.Length == 0)
        {
            return SHA256.HashData(content);
        }

        var combined = new byte[previousHash.Length + content.Length];
        Buffer.BlockCopy(previousHash, 0, combined, 0, previousHash.Length);
        Buffer.BlockCopy(content, 0, combined, previousHash.Length, content.Length);

        return SHA256.HashData(combined);
    }
}
