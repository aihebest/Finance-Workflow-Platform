using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Domain.Common;

namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// Cash Advance Form, modelled on DEL-AC-FRM-003 Rev 05.
///
/// The form carries its own SLA in print: "Please endeavour to retire within
/// 24 hours for transactions within local station state and 72 hours for
/// transactions out of station state. This transaction will be the liability
/// of recipient, till it will be justified."
///
/// Per Finance direction, those windows are measured in WORKING hours and the
/// clock starts at cash release. An unretired advance is a receivable against
/// the employee rather than merely a late task.
/// </summary>
public sealed class CashAdvanceRequest : Request
{
    /// <summary>Working hours allowed to retire an in-station advance.</summary>
    public const int InStationRetirementHours = 24;

    /// <summary>Working hours allowed to retire an out-of-station advance.</summary>
    public const int OutOfStationRetirementHours = 72;

    public CashAdvanceRequest()
    {
        ModuleKey = "CASH_ADVANCE";
        FormCode = "DEL-AC-FRM-003";
    }

    /// <summary>"Please approve a Cash Advance for the underlisted expense(s)".</summary>
    public string Purpose { get; set; } = string.Empty;

    public AllocationType AllocationType { get; set; } = AllocationType.CostCentre;

    public string? ProjectCode { get; set; }

    public string? CostCentreCode { get; set; }

    /// <summary>Not on the paper form as a field, but implied by the printed
    /// 24h/72h rule. Without it the retirement date cannot be computed.</summary>
    public StationScope StationScope { get; set; } = StationScope.InStation;

    public bool HasSupportingDocuments { get; set; }

    public string AmountInWords { get; set; } = string.Empty;

    public DateTimeOffset? CashReleasedAt { get; set; }

    /// <summary>Recipient's Acknowledgement. Recorded for the audit trail;
    /// the retirement clock starts at cash release, not here.</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public Guid? AcknowledgedByUserId { get; set; }

    public DateTimeOffset? RetirementDueDate { get; set; }

    public RetirementStatus RetirementStatus { get; set; } = RetirementStatus.NotDue;

    public Guid? PostedByUserId { get; set; }

    public Guid? AuthorisedByUserId { get; set; }

    /// <summary>Derived, not chosen -- see ApplyPaymentMethodPolicy. Cash
    /// release itself is a manual Finance action (ReleaseCashAsync); this
    /// only says which method the policy requires them to use.</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;

    public List<AdvanceLine> Lines { get; set; } = new();

    public List<GlPostingLine> GlPostingLines { get; set; } = new();

    /// <summary>Total retired so far via linked expense claims.</summary>
    public decimal RetiredAmountNgn { get; set; }

    public decimal RetirementBalanceNgn => TotalAmountNgn - RetiredAmountNgn;

    public decimal GlDebitTotal =>
        GlPostingLines.Where(l => l.Side == PostingSide.Debit).Sum(l => l.AmountNgn);

    public decimal GlCreditTotal =>
        GlPostingLines.Where(l => l.Side == PostingSide.Credit).Sum(l => l.AmountNgn);

    /// <summary>Working hours allowed to retire, per station scope.</summary>
    public int RetirementWindowHours => StationScope == StationScope.InStation
        ? InStationRetirementHours
        : OutOfStationRetirementHours;

    public void RecalculateTotals() =>
        TotalAmountNgn = Lines.Sum(l => l.AmountNgn);

    /// <summary>
    /// Derives cash versus bank transfer from the policy threshold, same rule
    /// and same policy key as ExpenseRequest.ApplyPaymentMethodPolicy, applied
    /// to TotalAmountNgn since an advance has no "net payable" concept of its
    /// own -- that only exists once a claim retires it.
    /// </summary>
    public void ApplyPaymentMethodPolicy(decimal thresholdNgn) =>
        PaymentMethod = TotalAmountNgn > thresholdNgn
            ? PaymentMethod.BankTransfer
            : PaymentMethod.Cash;

    public bool HasValidAllocation => AllocationType switch
    {
        AllocationType.Project => !string.IsNullOrWhiteSpace(ProjectCode),
        AllocationType.CostCentre => !string.IsNullOrWhiteSpace(CostCentreCode),
        _ => false
    };

    /// <summary>
    /// Whether this requester currently has another cash advance sitting in
    /// Overdue retirement status -- the SUBMIT guard's
    /// BLOCK_NEW_ADVANCE_WHEN_OVERDUE rule. This is a cross-request fact a
    /// plain POCO cannot answer on its own, so it is not computed here: it is
    /// staged by RequestActionService.RunTransitionAsync immediately before
    /// guard evaluation, the same pattern GlPostingLines uses for the POSTING
    /// guard on this same class.
    /// </summary>
    public bool HasOverdueAdvance { get; set; }

    /// <summary>
    /// Starts the retirement clock. Called when Finance releases the cash --
    /// not at approval, and not at recipient acknowledgement.
    ///
    /// The window is measured in WORKING hours against the supplied calendar.
    /// Note what that means in practice on a 9-hour working day: 24 working
    /// hours is 2.67 working days, and 72 working hours is 8 working days,
    /// which from a Friday afternoon release lands roughly twelve calendar
    /// days later. If Finance intended "72 hours" on the paper form to mean
    /// three calendar days, this is materially more generous and every overdue
    /// figure on the dashboard will read low. The window and its unit are both
    /// policy values so the answer can be changed without a deployment.
    /// </summary>
    public void ReleaseCash(
        DateTimeOffset releasedAt,
        IWorkingCalendar calendar,
        decimal? overrideWindowHours = null)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        CashReleasedAt = releasedAt;
        RetirementDueDate = calendar.AddWorkingHours(
            releasedAt, overrideWindowHours ?? RetirementWindowHours);
        RetirementStatus = RetirementStatus.NotDue;
    }

    /// <summary>
    /// Recipient's Acknowledgement. Recorded for the audit trail and for the
    /// printed form, but it no longer starts the clock -- release does.
    /// </summary>
    public void Acknowledge(Guid userId, DateTimeOffset acknowledgedAt)
    {
        AcknowledgedByUserId = userId;
        AcknowledgedAt = acknowledgedAt;
    }

    /// <summary>
    /// Recomputes retirement status. Called by the daily retirement sweep and
    /// whenever a linked expense claim is approved.
    /// </summary>
    public void RecalculateRetirementStatus(DateTimeOffset now)
    {
        if (RetirementBalanceNgn <= 0)
        {
            RetirementStatus = RetirementStatus.FullyRetired;
            return;
        }

        if (RetiredAmountNgn > 0)
        {
            RetirementStatus = RetirementDueDate.HasValue && now > RetirementDueDate.Value
                ? RetirementStatus.Overdue
                : RetirementStatus.PartiallyRetired;
            return;
        }

        if (RetirementDueDate is null)
        {
            RetirementStatus = RetirementStatus.NotDue;
            return;
        }

        RetirementStatus = now > RetirementDueDate.Value
            ? RetirementStatus.Overdue
            : RetirementStatus.Due;
    }

    /// <summary>See <see cref="Request.Accessors"/> -- FieldNames and
    /// TryGetField both read this same dictionary so they cannot drift.</summary>
    private static readonly Dictionary<string, Func<CashAdvanceRequest, object?>> Accessors =
        new(StringComparer.Ordinal)
        {
            [nameof(Purpose)] = r => r.Purpose,
            [nameof(AllocationType)] = r => r.AllocationType.ToString(),
            [nameof(ProjectCode)] = r => r.ProjectCode,
            [nameof(CostCentreCode)] = r => r.CostCentreCode,
            [nameof(StationScope)] = r => r.StationScope.ToString(),
            [nameof(HasSupportingDocuments)] = r => r.HasSupportingDocuments,
            [nameof(HasValidAllocation)] = r => r.HasValidAllocation,
            [nameof(HasOverdueAdvance)] = r => r.HasOverdueAdvance,
            [nameof(AcknowledgedAt)] = r => r.AcknowledgedAt,
            [nameof(RetirementDueDate)] = r => r.RetirementDueDate,
            [nameof(RetirementStatus)] = r => r.RetirementStatus.ToString(),
            [nameof(RetiredAmountNgn)] = r => r.RetiredAmountNgn,
            [nameof(RetirementBalanceNgn)] = r => r.RetirementBalanceNgn,
            [nameof(PostedByUserId)] = r => r.PostedByUserId,
            [nameof(AuthorisedByUserId)] = r => r.AuthorisedByUserId,
            [nameof(PaymentMethod)] = r => r.PaymentMethod.ToString(),
            [nameof(GlDebitTotal)] = r => r.GlDebitTotal,
            [nameof(GlCreditTotal)] = r => r.GlCreditTotal,
            ["GlLineCount"] = r => (decimal)r.GlPostingLines.Count,
            ["LineCount"] = r => (decimal)r.Lines.Count,
        };

    /// <summary>This module's own fields. A guard's full field surface for
    /// CASH_ADVANCE is this union <see cref="Request.FieldNames"/>.</summary>
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

public sealed class AdvanceLine
{
    public Guid LineId { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public int LineNumber { get; set; }

    public string Description { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = Money.BaseCurrency;

    public decimal Amount { get; set; }

    public decimal FxRate { get; set; } = 1.0m;

    public DateOnly FxRateDate { get; set; }

    public decimal AmountNgn { get; set; }

    public void RecalculateNgn() =>
        AmountNgn = Math.Round(Amount * FxRate, 2, MidpointRounding.AwayFromZero);
}
