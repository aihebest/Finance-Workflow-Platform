namespace Desicon.Workflow.Domain.People;

/// <summary>
/// Owns the DepartmentHeadOf actor resolver and the DefaultCostCentreCode an
/// expense line falls back to when the requester has none of their own.
/// </summary>
public sealed class Department
{
    public int Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Nullable: a department can exist administratively before a
    /// head is appointed, and DepartmentHeadOf simply resolves to an empty
    /// set until one is.</summary>
    public Guid? DepartmentHeadId { get; set; }

    public bool IsActive { get; set; } = true;
}
