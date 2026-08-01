using Desicon.Workflow.Core.Guards;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Guards;

public sealed class GuardEvaluatorTests
{
    private readonly GuardEvaluator _evaluator = new();

    private sealed class Fields : IGuardFieldSource
    {
        private readonly Dictionary<string, object?> _values;

        public Fields(Dictionary<string, object?> values) => _values = values;

        public bool TryGetField(string name, out object? value) =>
            _values.TryGetValue(name, out value);
    }

    private bool Eval(string expression, Dictionary<string, object?>? fields = null) =>
        _evaluator.EvaluateBoolean(
            GuardParser.Parse(expression),
            new Fields(fields ?? new Dictionary<string, object?>()));

    // ── Operator precedence ────────────────────────────────────────────────

    [Theory]
    [InlineData("true || false && false", true)]   // && binds tighter than ||
    [InlineData("(true || false) && false", false)]
    [InlineData("1 + 2 == 3", true)]               // + binds tighter than ==
    [InlineData("2 > 1 == true", true)]
    [InlineData("!false && true", true)]
    [InlineData("-5 + 10 > 4", true)]
    public void Respects_operator_precedence(string expression, bool expected) =>
        Eval(expression).Should().Be(expected);

    // ── The maker-checker guard, which is the one that must never misfire ──

    [Fact]
    public void Maker_checker_guard_blocks_same_user()
    {
        var user = Guid.NewGuid();

        Eval("ActorId != PostedByUserId", new Dictionary<string, object?>
        {
            ["ActorId"] = user,
            ["PostedByUserId"] = user
        }).Should().BeFalse("the inputer must not also be the authoriser");
    }

    [Fact]
    public void Maker_checker_guard_allows_different_users() =>
        Eval("ActorId != PostedByUserId", new Dictionary<string, object?>
        {
            ["ActorId"] = Guid.NewGuid(),
            ["PostedByUserId"] = Guid.NewGuid()
        }).Should().BeTrue();

    [Fact]
    public void Guid_compares_equal_to_its_string_form()
    {
        var id = Guid.NewGuid();

        Eval("ActorId == PostedByUserId", new Dictionary<string, object?>
        {
            ["ActorId"] = id,
            ["PostedByUserId"] = id.ToString()
        }).Should().BeTrue();
    }

    // ── The expense module's approve branch ────────────────────────────────

    [Theory]
    [InlineData(5000, true)]
    [InlineData(0, true)]
    [InlineData(-2500, false)]
    public void Net_payable_branch_selects_correct_path(decimal netPayable, bool goesToPosting) =>
        Eval("NetPayableNgn >= 0", new Dictionary<string, object?>
        {
            ["NetPayableNgn"] = netPayable
        }).Should().Be(goesToPosting);

    [Fact]
    public void Refund_guard_uses_absolute_value_of_negative_net_payable() =>
        Eval("RefundReceivedAmountNgn == Abs(NetPayableNgn)", new Dictionary<string, object?>
        {
            ["RefundReceivedAmountNgn"] = 2500m,
            ["NetPayableNgn"] = -2500m
        }).Should().BeTrue();

    [Fact]
    public void Balanced_posting_guard_requires_equal_debits_and_credits() =>
        Eval("GlDebitTotal == GlCreditTotal && GlLineCount >= 2", new Dictionary<string, object?>
        {
            ["GlDebitTotal"] = 14000m,
            ["GlCreditTotal"] = 14000m,
            ["GlLineCount"] = 2m
        }).Should().BeTrue();

    // ── Null handling ──────────────────────────────────────────────────────

    [Fact]
    public void Null_comparison_works_for_reference_capture_fields()
    {
        Eval("TreasuryNumber != null", new Dictionary<string, object?>
        {
            ["TreasuryNumber"] = null
        }).Should().BeFalse();

        Eval("TreasuryNumber != null", new Dictionary<string, object?>
        {
            ["TreasuryNumber"] = "TR-2026-0091"
        }).Should().BeTrue();
    }

    [Fact]
    public void Short_circuit_prevents_evaluating_right_side_when_left_is_false() =>
        // AdvanceAmountNgn is null, which would throw if evaluated. It must not be.
        Eval("RetiresAdvanceId != null && AdvanceAmountNgn > 0",
            new Dictionary<string, object?>
            {
                ["RetiresAdvanceId"] = null,
                ["AdvanceAmountNgn"] = null
            }).Should().BeFalse();

    [Fact]
    public void Ordering_against_null_is_rejected_rather_than_guessed()
    {
        var act = () => Eval("TotalAmountNgn > 0", new Dictionary<string, object?>
        {
            ["TotalAmountNgn"] = null
        });

        act.Should().Throw<GuardEvaluationException>()
            .WithMessage("*null*");
    }

    // ── Security posture ───────────────────────────────────────────────────

    [Theory]
    [InlineData("System.IO.File.Delete('x')")]
    [InlineData("Exec('rm -rf /')")]
    [InlineData("GetType()")]
    public void Rejects_functions_outside_the_allowlist(string expression)
    {
        var act = () => Eval(expression);

        act.Should().Throw<Exception>()
            .Which.Should().Match(e =>
                e is GuardEvaluationException || e is GuardSyntaxException);
    }

    [Fact]
    public void Unknown_field_fails_loudly_rather_than_defaulting()
    {
        // A misspelled field that silently evaluated false would block every
        // request in the state; silently true would wave them all through.
        var act = () => Eval("NetPayableNGN >= 0", new Dictionary<string, object?>
        {
            ["NetPayableNgn"] = 100m
        });

        act.Should().Throw<GuardEvaluationException>()
            .WithMessage("*unknown field*");
    }

    [Theory]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("'unterminated")]
    [InlineData("a & b")]
    [InlineData("")]
    public void Rejects_malformed_expressions(string expression)
    {
        var act = () => Eval(expression);
        act.Should().Throw<GuardSyntaxException>();
    }

    [Fact]
    public void Non_boolean_result_is_rejected()
    {
        var act = () => Eval("1 + 1");
        act.Should().Throw<GuardEvaluationException>().WithMessage("*boolean*");
    }
}
