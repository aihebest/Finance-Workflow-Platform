namespace Desicon.Workflow.Domain.People;

/// <summary>
/// The identity behind every actor in the system. Requesters, approvers,
/// department heads and delegates are all employees -- there is no separate
/// "user" concept, because everyone who can act in the workflow is staff.
/// </summary>
public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Object id from the Entra ID (Azure AD) tenant. This, not
    /// StaffNumber, is what sign-in actually authenticates against.</summary>
    public string EntraObjectId { get; set; } = string.Empty;

    public string StaffNumber { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? Grade { get; set; }

    public int DepartmentId { get; set; }

    /// <summary>Self-referencing. Null at the top of the tree -- a
    /// managing director or equivalent with no line manager of their own.</summary>
    public Guid? LineManagerId { get; set; }

    public string? DefaultCostCentreCode { get; set; }

    public bool IsActive { get; set; } = true;
}
