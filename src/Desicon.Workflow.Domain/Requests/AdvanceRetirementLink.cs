namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// One claim's contribution toward retiring one advance. CashAdvanceRequest.
/// RetiredAmountNgn is the running total the RETIRE guard evaluates against,
/// but a running total alone cannot answer "which claims retired this advance,
/// and how much did each contribute" -- across several partial retirements
/// that question is only answerable from rows like this one.
/// </summary>
public sealed class AdvanceRetirementLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExpenseRequestId { get; set; }

    public Guid CashAdvanceRequestId { get; set; }

    public decimal AmountAppliedNgn { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
