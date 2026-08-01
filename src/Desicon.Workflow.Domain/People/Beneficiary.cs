using Desicon.Workflow.Domain.Common;

namespace Desicon.Workflow.Domain.People;

/// <summary>
/// Who an expense claim pays. DEL-AC-FRM-002 Rev 05 says "Please issue
/// payment in favour of company/staff", so this is deliberately broader than
/// Employee -- a vendor or a one-off "Other" payee never gets an Employee row.
/// </summary>
public sealed class Beneficiary
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public BeneficiaryType Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string BankName { get; set; } = string.Empty;

    public string BankAccountNumber { get; set; } = string.Empty;

    /// <summary>Set only when Type is Employee -- links the payee back to
    /// their staff record so an EmployeeActorResolver can, if a definition
    /// ever needs it, resolve "the beneficiary" to a person rather than just
    /// a bank account.</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>Who last set or edited BankName/BankAccountNumber, and when.
    /// Enforces the same maker-checker separation as
    /// Request.PostedByUserId/AuthorisedByUserId: whoever set these bank
    /// details cannot be the one who authorises payment to them (see
    /// RequestActionService.EvaluatePolicyViolation). Null only for a
    /// Beneficiary that has never had bank details set.</summary>
    public Guid? BankDetailsSetByUserId { get; set; }

    public DateTimeOffset? BankDetailsSetAt { get; set; }

    public bool HasBankDetails =>
        !string.IsNullOrWhiteSpace(BankName) && !string.IsNullOrWhiteSpace(BankAccountNumber);
}
