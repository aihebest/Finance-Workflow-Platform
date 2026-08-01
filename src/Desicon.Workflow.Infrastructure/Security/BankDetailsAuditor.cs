using System.Security.Cryptography;
using System.Text;
using Desicon.Workflow.Domain.People;
using Desicon.Workflow.Domain.Security;

namespace Desicon.Workflow.Infrastructure.Security;

/// <summary>
/// The single place Beneficiary.BankName/BankAccountNumber are ever set or
/// edited. Stamps who did it (for the bank-details maker-checker check in
/// RequestActionService.EvaluatePolicyViolation) and writes a SecurityEvent
/// carrying old/new value hashes rather than the values themselves -- the
/// event is a durable audit record, not a place to duplicate the account
/// number in yet another table.
/// </summary>
public interface IBankDetailsAuditor
{
    /// <summary>
    /// No-ops if newBankName/newBankAccountNumber match what is already on
    /// the beneficiary -- re-saving an unchanged value is not a "change" and
    /// must not overwrite BankDetailsSetByUserId with whoever merely
    /// re-submitted the same form.
    /// </summary>
    Task ApplyChangeAsync(
        Beneficiary beneficiary,
        string newBankName,
        string newBankAccountNumber,
        Guid requestId,
        string moduleKey,
        Guid actorId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

public sealed class BankDetailsAuditor : IBankDetailsAuditor
{
    private readonly ISecurityEventWriter _securityEvents;

    public BankDetailsAuditor(ISecurityEventWriter securityEvents)
    {
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
    }

    public Task ApplyChangeAsync(
        Beneficiary beneficiary,
        string newBankName,
        string newBankAccountNumber,
        Guid requestId,
        string moduleKey,
        Guid actorId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beneficiary);

        var oldBankName = beneficiary.BankName;
        var oldBankAccountNumber = beneficiary.BankAccountNumber;

        if (string.Equals(oldBankName, newBankName, StringComparison.Ordinal) &&
            string.Equals(oldBankAccountNumber, newBankAccountNumber, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        beneficiary.BankName = newBankName;
        beneficiary.BankAccountNumber = newBankAccountNumber;
        beneficiary.BankDetailsSetByUserId = actorId;
        beneficiary.BankDetailsSetAt = occurredAtUtc;

        var detail =
            $"BankName {Hash(oldBankName)} -> {Hash(newBankName)}; " +
            $"BankAccountNumber {Hash(oldBankAccountNumber)} -> {Hash(newBankAccountNumber)}";

        var securityEvent = new SecurityEvent
        {
            RequestId = requestId,
            ModuleKey = moduleKey,
            Action = "SET_BANK_DETAILS",
            Reason = "BankDetailsChanged",
            Detail = detail,
            AttemptedByUserId = actorId,
            OccurredAtUtc = occurredAtUtc
        };

        return _securityEvents.WriteAsync(securityEvent, cancellationToken);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
