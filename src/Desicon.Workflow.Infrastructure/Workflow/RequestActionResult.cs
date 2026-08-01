using Desicon.Workflow.Core.Engine;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// What RequestActionService did (or, on a replay, did the first time it was
/// called with this idempotency key).
/// </summary>
public sealed record RequestActionResult(
    TransitionOutcome Outcome,
    string? FromState,
    string? ToState,
    string? Message,
    long? AuditEventId,
    DateTimeOffset? SlaDueAt,
    bool WasIdempotentReplay = false)
{
    public bool Succeeded => Outcome == TransitionOutcome.Succeeded;
}
