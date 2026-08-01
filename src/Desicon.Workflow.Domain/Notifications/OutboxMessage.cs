namespace Desicon.Workflow.Domain.Notifications;

/// <summary>
/// Transactional outbox row. Written in the same database transaction as the
/// state change and audit event that caused it, so a notification is never
/// promised for a transition that did not actually commit, and a committed
/// transition never silently loses its notification to a crash between the
/// commit and the send.
/// </summary>
public sealed class OutboxMessage
{
    public long OutboxMessageId { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>Matches a WorkflowDefinition.Notifications[].Template, e.g.
    /// "action-required", "returned-for-correction".</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>JSON array of the recipient specifiers from the workflow
    /// definition (e.g. ["CurrentActor"], ["Requester","Beneficiary"]).
    /// Resolving a specifier to an actual address is the dispatcher's job --
    /// there is no Employee directory yet to resolve against.</summary>
    public string RecipientRolesJson { get; set; } = "[]";

    public string PayloadJson { get; set; } = "{}";

    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

public enum OutboxMessageStatus
{
    Pending,
    Dispatched,
    Failed
}
