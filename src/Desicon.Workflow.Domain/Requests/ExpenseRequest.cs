using Desicon.Workflow.Domain.Common;

namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// Expense Form, modelled directly on DEL-AC-FRM-002 Rev 05.
///
/// The important structural point: this form is also the retirement instrument
/// for a cash advance. The paper form nets "Cash Advance Taken" off the total
/// to reach "Net Payable", so Expense and Cash Advance are one lifecycle rather
/// than two independent modules.
/// </summary>
public sealed class ExpenseRequest : Request
{
    public ExpenseRequest()
    {
        ModuleKey = "EXPENSE";
        FormCode = "DEL-AC-FRM-002";
    }

    /// <summary>Payee. Not necessarily the requester -- the form says "Please
    /// issue payment in favour of company/staff", so this can be a vendor.</summary>
    public Guid BeneficiaryId { get; set; }

    /// <summary>Set when this claim retires a cash advance.</summary>
    public Guid? RetiresAdvanceId { get; set; }

    /// <summary>"Cash Advance Taken" on the paper form.</summary>
    public decimal AdvanceAmountNgn { get; set; }

    public ReceiptStatus ReceiptStatus { get; set; } = ReceiptStatus.No;

    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    /// <summary>Auto-generated. On paper this is an anti-tamper control; the
    /// digital record cannot be altered post-submission anyway, but the printed
    /// PDF is what gets filed and auditors expect it.</summary>
    public string AmountInWords { get; set; } = string.Empty;

    /// <summary>Inputer, from the ACF block. Must differ from the authoriser.</summary>
    public Guid? PostedByUserId { get; set; }

    /// <summary>Authoriser, from the ACF block. Must differ from the inputer.</summary>
    public Guid? AuthorisedByUserId { get; set; }

    public DateTimeOffset? PostedAt { get; set; }

    public string? PaymentReference { get; set; }

    public DateTimeOffset? PaymentDate { get; set; }

    /// <summary>Recipient's Acknowledgement block. This, not Finance marking
    /// the item paid, is what closes the request.</summary>
    public Guid? AcknowledgedByUserId { get; set; }

    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>Set when net payable is negative and the employee has returned
    /// the over-drawn amount.</summary>
    public decimal? RefundReceivedAmountNgn { get; set; }

    public List<ExpenseLine> Lines { get; set; } = new();

    public List<GlPostingLine> GlPostingLines { get; set; } = new();

    /// <summary>"Net Payable" on the paper form. Signed: a negative value means
    /// the employee spent less than the advance and owes the difference back.
    /// That path is the one most easily skipped on paper, and it is where money
    /// leaks.</summary>
    public decimal NetPayableNgn => TotalAmountNgn - AdvanceAmountNgn;

    public bool IsRefundDue => NetPayableNgn < 0;

    public decimal GlDebitTotal =>
        GlPostingLines.Where(l => l.Side == PostingSide.Debit).Sum(l => l.AmountNgn);

    public decimal GlCreditTotal =>
        GlPostingLines.Where(l => l.Side == PostingSide.Credit).Sum(l => l.AmountNgn);

    public void RecalculateTotals() =>
        TotalAmountNgn = Lines.Sum(l => l.AmountNgn);

    /// <summary>
    /// Derives cash versus bank transfer from the policy threshold. The paper
    /// form footer states NGN 30,000 at Rev 05, but the figure is passed in
    /// rather than hardcoded so a policy change does not need a deployment --
    /// and so a historical request reprints under the threshold that applied
    /// when it was raised.
    /// </summary>
    public void ApplyPaymentMethodPolicy(decimal thresholdNgn) =>
        PaymentMethod = NetPayableNgn > thresholdNgn
            ? PaymentMethod.BankTransfer
            : PaymentMethod.Cash;

    /// <summary>See <see cref="Request.Accessors"/> -- FieldNames and
    /// TryGetField both read this same dictionary so they cannot drift.</summary>
    private static readonly Dictionary<string, Func<ExpenseRequest, object?>> Accessors =
        new(StringComparer.Ordinal)
        {
            [nameof(BeneficiaryId)] = r => r.BeneficiaryId,
            [nameof(RetiresAdvanceId)] = r => r.RetiresAdvanceId,
            [nameof(AdvanceAmountNgn)] = r => r.AdvanceAmountNgn,
            [nameof(NetPayableNgn)] = r => r.NetPayableNgn,
            [nameof(ReceiptStatus)] = r => r.ReceiptStatus.ToString(),
            [nameof(PaymentMethod)] = r => r.PaymentMethod.ToString(),
            [nameof(PostedByUserId)] = r => r.PostedByUserId,
            [nameof(AuthorisedByUserId)] = r => r.AuthorisedByUserId,
            [nameof(AcknowledgedByUserId)] = r => r.AcknowledgedByUserId,
            [nameof(RefundReceivedAmountNgn)] = r => r.RefundReceivedAmountNgn,
            [nameof(GlDebitTotal)] = r => r.GlDebitTotal,
            [nameof(GlCreditTotal)] = r => r.GlCreditTotal,
            ["GlLineCount"] = r => (decimal)r.GlPostingLines.Count,
            ["LineCount"] = r => (decimal)r.Lines.Count,
        };

    /// <summary>This module's own fields. A guard's full field surface for
    /// EXPENSE is this union <see cref="Request.FieldNames"/>.</summary>
    public new static IReadOnlySet<string> FieldNames { get; } =
        Accessors.Keys.ToHashSet(StringComparer.Ordinal);

    public override bool TryGetField(string name, out object? value)
    {
        if (Accessors.TryGetValue(name, out var accessor))
        {
            value = accessor(this);
            return true;
        }

        return base.TryGetField(name, out value);
    }
}

/// <summary>
/// One row of the "Details of Expense" table. Cost allocation lives on the
/// line, not the header -- DEL-AC-FRM-002 puts Project Code and Cost Center
/// Code inside the line-item table, and exactly one of them must be populated.
/// </summary>
public sealed class ExpenseLine
{
    public Guid LineId { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public int LineNumber { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Not on the paper form. Added so a June expense cannot land in
    /// the July ledger; should go into the next paper revision too.</summary>
    public DateOnly ExpenseDate { get; set; }

    /// <summary>Not on the paper form. Required by the reporting goals and not
    /// reconstructible from free-text description.</summary>
    public int? ExpenseCategoryId { get; set; }

    public string? ProjectCode { get; set; }

    public string? CostCentreCode { get; set; }

    public string CurrencyCode { get; set; } = Money.BaseCurrency;

    public decimal Amount { get; set; }

    public decimal FxRate { get; set; } = 1.0m;

    public DateOnly FxRateDate { get; set; }

    /// <summary>Persisted at capture, never recomputed on read.</summary>
    public decimal AmountNgn { get; set; }

    public bool HasValidAllocation =>
        string.IsNullOrWhiteSpace(ProjectCode) ^ string.IsNullOrWhiteSpace(CostCentreCode);

    public void RecalculateNgn() =>
        AmountNgn = Math.Round(Amount * FxRate, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A line of the journal voucher assembled in the "FOR ACF DEPT USE ONLY"
/// block. Debits must equal credits before the request can leave posting.
/// </summary>
public sealed class GlPostingLine
{
    public Guid PostingLineId { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public PostingSide Side { get; set; }

    public string AccountNumber { get; set; } = string.Empty;

    public string? Narration { get; set; }

    public decimal AmountNgn { get; set; }
}
