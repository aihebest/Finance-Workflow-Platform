using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Requests;

/// <summary>
/// Regression coverage for the POSTING guard, copied verbatim from
/// modules/expense-reimbursement.workflow.json and modules/cash-advance.workflow.json:
///
///   GlDebitTotal == GlCreditTotal &amp;&amp; GlLineCount >= 2 &amp;&amp; JournalVoucherNumber != null
///
/// Exercised against real GlPostingLine collections (not a hand-fed field
/// dictionary) so this also proves out the fixed GlPostingLines TPT loading --
/// before that fix, GlDebitTotal/GlCreditTotal/GlLineCount had no lines to sum
/// and the guard failed closed for the wrong reason.
/// </summary>
public sealed class PostingGuardTests
{
    private const string PostingGuard =
        "GlDebitTotal == GlCreditTotal && GlLineCount >= 2 && JournalVoucherNumber != null";

    private readonly GuardEvaluator _evaluator = new();

    private bool Evaluate(Request request) =>
        _evaluator.EvaluateBoolean(GuardParser.Parse(PostingGuard), request);

    private static GlPostingLine Line(PostingSide side, decimal amount) => new()
    {
        Side = side,
        AccountNumber = "1000",
        AmountNgn = amount
    };

    // ── ExpenseRequest ──────────────────────────────────────────────────────

    [Fact]
    public void Expense_posting_guard_blocks_on_empty_line_set()
    {
        var request = new ExpenseRequest { JournalVoucherNumber = "JV-0001" };

        Evaluate(request).Should().BeFalse();
    }

    [Fact]
    public void Expense_posting_guard_blocks_on_unbalanced_lines()
    {
        var request = new ExpenseRequest { JournalVoucherNumber = "JV-0001" };
        request.GlPostingLines.Add(Line(PostingSide.Debit, 14000m));
        request.GlPostingLines.Add(Line(PostingSide.Credit, 13000m));

        Evaluate(request).Should().BeFalse();
    }

    [Fact]
    public void Expense_posting_guard_passes_on_balanced_lines_with_voucher_number()
    {
        var request = new ExpenseRequest { JournalVoucherNumber = "JV-0001" };
        request.GlPostingLines.Add(Line(PostingSide.Debit, 14000m));
        request.GlPostingLines.Add(Line(PostingSide.Credit, 14000m));

        Evaluate(request).Should().BeTrue();
    }

    // ── CashAdvanceRequest ──────────────────────────────────────────────────

    [Fact]
    public void CashAdvance_posting_guard_blocks_on_empty_line_set()
    {
        var request = new CashAdvanceRequest { JournalVoucherNumber = "JV-0002" };

        Evaluate(request).Should().BeFalse();
    }

    [Fact]
    public void CashAdvance_posting_guard_blocks_on_unbalanced_lines()
    {
        var request = new CashAdvanceRequest { JournalVoucherNumber = "JV-0002" };
        request.GlPostingLines.Add(Line(PostingSide.Debit, 5000m));
        request.GlPostingLines.Add(Line(PostingSide.Credit, 4500m));

        Evaluate(request).Should().BeFalse();
    }

    [Fact]
    public void CashAdvance_posting_guard_passes_on_balanced_lines_with_voucher_number()
    {
        var request = new CashAdvanceRequest { JournalVoucherNumber = "JV-0002" };
        request.GlPostingLines.Add(Line(PostingSide.Debit, 5000m));
        request.GlPostingLines.Add(Line(PostingSide.Credit, 5000m));

        Evaluate(request).Should().BeTrue();
    }
}
