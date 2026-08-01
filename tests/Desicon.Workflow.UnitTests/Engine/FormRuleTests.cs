using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Engine;

/// <summary>
/// These tests encode the rules read off DEL-AC-FRM-002 and DEL-AC-FRM-003.
/// If a rule here is wrong, the form was misread -- so each test names the
/// clause it comes from.
/// </summary>
public sealed class FormRuleTests
{
    private static ExpenseLine Line(decimal ngn) => new()
    {
        Description = "Test line",
        CurrencyCode = "NGN",
        Amount = ngn,
        FxRate = 1.0m,
        AmountNgn = ngn
    };

    // ── "Total - Less Advance Taken = Net Payable" ─────────────────────────

    [Fact]
    public void Net_payable_is_positive_when_spend_exceeds_advance()
    {
        var claim = new ExpenseRequest { AdvanceAmountNgn = 10_000m };
        claim.Lines.Add(Line(14_000m));
        claim.RecalculateTotals();

        claim.NetPayableNgn.Should().Be(4_000m);
        claim.IsRefundDue.Should().BeFalse();
    }

    [Fact]
    public void Net_payable_is_zero_when_spend_matches_advance_exactly()
    {
        var claim = new ExpenseRequest { AdvanceAmountNgn = 14_000m };
        claim.Lines.Add(Line(14_000m));
        claim.RecalculateTotals();

        claim.NetPayableNgn.Should().Be(0m);
        claim.IsRefundDue.Should().BeFalse();
    }

    [Fact]
    public void Negative_net_payable_means_the_employee_owes_a_refund()
    {
        // The path the paper process quietly skips, and the one the original
        // brief had no state for.
        var claim = new ExpenseRequest { AdvanceAmountNgn = 14_000m };
        claim.Lines.Add(Line(9_500m));
        claim.RecalculateTotals();

        claim.NetPayableNgn.Should().Be(-4_500m);
        claim.IsRefundDue.Should().BeTrue();
    }

    // ── "Amount above NGN 30,000 net payable will be transferred to bank" ──

    [Theory]
    [InlineData(30_001, PaymentMethod.BankTransfer)]
    [InlineData(30_000, PaymentMethod.Cash)]      // "above", so 30,000 exactly is cash
    [InlineData(500, PaymentMethod.Cash)]
    public void Payment_method_follows_the_policy_threshold(
        decimal netPayable, PaymentMethod expected)
    {
        var claim = new ExpenseRequest();
        claim.Lines.Add(Line(netPayable));
        claim.RecalculateTotals();

        claim.ApplyPaymentMethodPolicy(thresholdNgn: 30_000m);

        claim.PaymentMethod.Should().Be(expected);
    }

    // ── Same threshold, applied to CashAdvanceRequest.TotalAmountNgn: an
    // advance has no net-payable concept of its own, so
    // ApplyPaymentMethodPolicy reads the total instead (see
    // cash-advance.workflow.json's PAYMENT_METHOD_THRESHOLD_NGN note). ────

    [Theory]
    [InlineData(30_001, PaymentMethod.BankTransfer)]
    [InlineData(30_000, PaymentMethod.Cash)]
    [InlineData(500, PaymentMethod.Cash)]
    public void CashAdvance_payment_method_follows_the_same_policy_threshold(
        decimal totalNgn, PaymentMethod expected)
    {
        var advance = new CashAdvanceRequest();
        advance.Lines.Add(new AdvanceLine { Amount = totalNgn, AmountNgn = totalNgn });
        advance.RecalculateTotals();

        advance.ApplyPaymentMethodPolicy(thresholdNgn: 30_000m);

        advance.PaymentMethod.Should().Be(expected);
    }

    // ── "Project Code / Cost Center Code" allocation is exclusive ──────────

    [Theory]
    [InlineData("PRJ-001", null, true)]
    [InlineData(null, "CC-100", true)]
    [InlineData("PRJ-001", "CC-100", false)]
    [InlineData(null, null, false)]
    public void Expense_line_requires_exactly_one_allocation_code(
        string? project, string? costCentre, bool valid)
    {
        var line = Line(1_000m);
        line.ProjectCode = project;
        line.CostCentreCode = costCentre;

        line.HasValidAllocation.Should().Be(valid);
    }

    // ── "Retire within 24 hours in-station, 72 hours out of station" ───────

    [Theory]
    [InlineData(StationScope.InStation, 24)]
    [InlineData(StationScope.OutOfStation, 72)]
    public void Retirement_window_follows_station_scope(StationScope scope, int expectedHours)
    {
        var advance = new CashAdvanceRequest { StationScope = scope };
        advance.RetirementWindowHours.Should().Be(expectedHours);
    }

    // Retirement-clock start point and the overdue transition are covered by
    // RetirementClockTests in WorkingCalendarTests.cs, against the corrected
    // release-based, working-hours design (Finance direction of 2026-07-31).

    [Fact]
    public void Partial_retirement_leaves_a_balance()
    {
        var advance = new CashAdvanceRequest();
        advance.Lines.Add(new AdvanceLine { Amount = 14_000m, AmountNgn = 14_000m });
        advance.RecalculateTotals();
        advance.Acknowledge(Guid.NewGuid(), DateTimeOffset.UtcNow);

        advance.RetiredAmountNgn = 9_000m;
        advance.RecalculateRetirementStatus(DateTimeOffset.UtcNow);

        advance.RetirementBalanceNgn.Should().Be(5_000m);
        advance.RetirementStatus.Should().Be(RetirementStatus.PartiallyRetired);
    }

    [Fact]
    public void Full_retirement_clears_the_balance()
    {
        var advance = new CashAdvanceRequest();
        advance.Lines.Add(new AdvanceLine { Amount = 14_000m, AmountNgn = 14_000m });
        advance.RecalculateTotals();
        advance.RetiredAmountNgn = 14_000m;

        advance.RecalculateRetirementStatus(DateTimeOffset.UtcNow);

        advance.RetirementBalanceNgn.Should().Be(0m);
        advance.RetirementStatus.Should().Be(RetirementStatus.FullyRetired);
    }

    // ── Money ─────────────────────────────────────────────────────────────

    [Fact]
    public void Foreign_currency_converts_at_the_captured_rate()
    {
        var money = Money.Foreign("USD", 250m, 1_540.50m, new DateOnly(2026, 7, 15));
        money.AmountNgn.Should().Be(385_125.00m);
    }

    [Fact]
    public void Naira_amount_must_carry_a_rate_of_one()
    {
        var act = () => Money.Foreign("NGN", 100m, 1.5m, new DateOnly(2026, 7, 15));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rounding_is_half_away_from_zero_so_the_printed_form_foots()
    {
        var money = Money.Foreign("USD", 1m, 1_540.505m, new DateOnly(2026, 7, 15));
        money.AmountNgn.Should().Be(1_540.51m);
    }

    [Fact]
    public void Header_total_equals_the_sum_of_rounded_lines()
    {
        var claim = new ExpenseRequest();

        foreach (var amount in new[] { 1_540.505m, 2_310.257m, 990.121m })
        {
            var line = Line(0m);
            line.CurrencyCode = "USD";
            line.Amount = amount;
            line.FxRate = 1.0m;
            line.RecalculateNgn();
            claim.Lines.Add(line);
        }

        claim.RecalculateTotals();

        claim.TotalAmountNgn.Should().Be(1_540.51m + 2_310.26m + 990.12m);
    }
}
