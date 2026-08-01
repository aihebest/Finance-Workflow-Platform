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
