namespace Desicon.Workflow.Domain.Common;

/// <summary>
/// Tri-state, mirroring the "Attached receipts: Yes / No / Incomplete" tick on
/// DEL-AC-FRM-002 Rev 05. Incomplete is not a rejection -- it routes the
/// request back to the requester for correction, keeping its number and history.
/// </summary>
public enum ReceiptStatus
{
    No = 0,
    Incomplete = 1,
    Yes = 2
}

/// <summary>
/// Derived, never chosen by the user. DEL-AC-FRM-002 Rev 05 states that net
/// payable above NGN 30,000 is transferred to the employee bank account. The
/// threshold is a policy value with effective dating, not a constant.
/// </summary>
public enum PaymentMethod
{
    Cash = 0,
    BankTransfer = 1
}

/// <summary>
/// Drives the retirement clock. DEL-AC-FRM-003 Rev 05 requires retirement
/// within 24 hours in-station and 72 hours out of station.
/// </summary>
public enum StationScope
{
    InStation = 0,
    OutOfStation = 1
}

/// <summary>
/// Mutually exclusive, mirroring the "Projects Specific / Non Projects
/// Specific" tick on DEL-AC-FRM-003 Rev 05.
/// </summary>
public enum AllocationType
{
    Project = 0,
    CostCentre = 1
}

public enum RetirementStatus
{
    NotDue = 0,
    Due = 1,
    Overdue = 2,
    PartiallyRetired = 3,
    FullyRetired = 4
}

public enum BeneficiaryType
{
    Employee = 0,
    Vendor = 1,
    Other = 2
}

public enum PostingSide
{
    Debit = 0,
    Credit = 1
}
