namespace Desicon.Workflow.Domain.Security;

/// <summary>
/// A record of a denied action attempt. Deliberately separate from
/// AuditEvent, which records what the workflow did -- this records what
/// someone tried and was refused, including refusals that never touch the
/// request's own transaction (a denial has no FromState/ToState change to
/// chain a hash against, and it must survive the rollback of the very
/// transaction it is reporting on). See ISecurityEventWriter for the
/// durability guarantee that requires.
/// </summary>
public sealed class SecurityEvent
{
    public long SecurityEventId { get; set; }

    public Guid RequestId { get; set; }

    public string ModuleKey { get; set; } = string.Empty;

    /// <summary>The action the caller attempted, e.g. "APPROVE".</summary>
    public string Action { get; set; } = string.Empty;

    public string? FromState { get; set; }

    /// <summary>NotAuthorised, GuardFailed, or PolicyViolation -- see
    /// Core.Engine.TransitionOutcome.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Free-text detail: the guard message, or which
    /// defence-in-depth check fired (self-approval / maker-checker).</summary>
    public string? Detail { get; set; }

    public Guid AttemptedByUserId { get; set; }

    /// <summary>Populated when the attempt was made under a delegation.</summary>
    public Guid? OnBehalfOfUserId { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }
}
