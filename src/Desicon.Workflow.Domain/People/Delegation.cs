namespace Desicon.Workflow.Domain.People;

/// <summary>
/// "While I am on leave, act for me." An active delegation extends actor
/// resolution: wherever FromEmployeeId would resolve, ToEmployeeId resolves
/// too, for the window between StartsAt and EndsAt. It does not transfer
/// identity -- the delegate acts as themselves, on the delegator's behalf,
/// and AuditEvent.OnBehalfOfUserId is how that distinction survives to the
/// trail.
/// </summary>
public sealed class Delegation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FromEmployeeId { get; set; }

    public Guid ToEmployeeId { get; set; }

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset EndsAt { get; set; }

    public string? Reason { get; set; }

    /// <summary>A manual off switch independent of the date window -- covers
    /// a delegation being revoked early (the delegator returned sooner than
    /// planned) without deleting the historical record.</summary>
    public bool IsActive { get; set; } = true;

    public bool CoversInstant(DateTimeOffset instant) =>
        IsActive && instant >= StartsAt && instant <= EndsAt;
}
