using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Requests;

/// <summary>
/// Regression coverage for the two TryGetField bugs the guard-field
/// reachability check surfaced: HasValidAllocation was missing entirely, and
/// HasOverdueAdvance did not exist. Both must resolve through TryGetField --
/// not just exist as CLR properties -- since that is what GuardEvaluator
/// actually calls at runtime.
/// </summary>
public sealed class RequestFieldAccessTests
{
    [Theory]
    [InlineData(AllocationType.Project, "PRJ-01", null, true)]
    [InlineData(AllocationType.Project, null, null, false)]
    [InlineData(AllocationType.CostCentre, null, "CC-01", true)]
    [InlineData(AllocationType.CostCentre, null, null, false)]
    public void HasValidAllocation_resolves_through_TryGetField(
        AllocationType allocationType, string? projectCode, string? costCentreCode, bool expected)
    {
        var request = new CashAdvanceRequest
        {
            AllocationType = allocationType,
            ProjectCode = projectCode,
            CostCentreCode = costCentreCode
        };

        request.TryGetField(nameof(CashAdvanceRequest.HasValidAllocation), out var value)
            .Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void HasOverdueAdvance_resolves_through_TryGetField()
    {
        var request = new CashAdvanceRequest { HasOverdueAdvance = true };

        request.TryGetField(nameof(CashAdvanceRequest.HasOverdueAdvance), out var value)
            .Should().BeTrue();
        value.Should().Be(true);
    }

    [Fact]
    public void HasOverdueAdvance_defaults_to_false_when_not_staged()
    {
        var request = new CashAdvanceRequest();

        request.TryGetField(nameof(CashAdvanceRequest.HasOverdueAdvance), out var value)
            .Should().BeTrue();
        value.Should().Be(false);
    }

    [Fact]
    public void CashAdvanceRequest_FieldNames_includes_both_fixed_fields()
    {
        CashAdvanceRequest.FieldNames.Should().Contain(nameof(CashAdvanceRequest.HasValidAllocation));
        CashAdvanceRequest.FieldNames.Should().Contain(nameof(CashAdvanceRequest.HasOverdueAdvance));
    }

    // ── BeneficiaryHasBankDetails: staged by RequestActionService.RunTransitionAsync,
    // same shape as HasOverdueAdvance above -- a plain ExpenseRequest cannot
    // answer this on its own, so the guard must still see it via TryGetField.

    [Fact]
    public void BeneficiaryHasBankDetails_resolves_through_TryGetField()
    {
        var request = new ExpenseRequest { BeneficiaryHasBankDetails = true };

        request.TryGetField(nameof(ExpenseRequest.BeneficiaryHasBankDetails), out var value)
            .Should().BeTrue();
        value.Should().Be(true);
    }

    [Fact]
    public void BeneficiaryHasBankDetails_defaults_to_false_when_not_staged()
    {
        var request = new ExpenseRequest();

        request.TryGetField(nameof(ExpenseRequest.BeneficiaryHasBankDetails), out var value)
            .Should().BeTrue();
        value.Should().Be(false);
    }

    [Fact]
    public void ExpenseRequest_FieldNames_includes_BeneficiaryHasBankDetails()
    {
        ExpenseRequest.FieldNames.Should().Contain(nameof(ExpenseRequest.BeneficiaryHasBankDetails));
    }

    // ── BeneficiaryBankDetailsSetByUserId: same staging shape as
    // BeneficiaryHasBankDetails above -- the AUTHORISE guard's
    // "ActorId != BeneficiaryBankDetailsSetByUserId" clause needs this via
    // TryGetField, not reflection.

    [Fact]
    public void BeneficiaryBankDetailsSetByUserId_resolves_through_TryGetField()
    {
        var setter = Guid.NewGuid();
        var request = new ExpenseRequest { BeneficiaryBankDetailsSetByUserId = setter };

        request.TryGetField(nameof(ExpenseRequest.BeneficiaryBankDetailsSetByUserId), out var value)
            .Should().BeTrue();
        value.Should().Be(setter);
    }

    [Fact]
    public void BeneficiaryBankDetailsSetByUserId_defaults_to_null_when_not_staged()
    {
        var request = new ExpenseRequest();

        request.TryGetField(nameof(ExpenseRequest.BeneficiaryBankDetailsSetByUserId), out var value)
            .Should().BeTrue();
        value.Should().BeNull();
    }

    [Fact]
    public void ExpenseRequest_FieldNames_includes_BeneficiaryBankDetailsSetByUserId()
    {
        ExpenseRequest.FieldNames.Should().Contain(nameof(ExpenseRequest.BeneficiaryBankDetailsSetByUserId));
    }

    // ── CashAdvanceRequest.PaymentMethod: derived, not chosen -- see
    // FormRuleTests for ApplyPaymentMethodPolicy itself; this only covers
    // that the derived value resolves through TryGetField the same way any
    // other guard-readable field must.

    [Fact]
    public void CashAdvanceRequest_PaymentMethod_resolves_through_TryGetField()
    {
        var request = new CashAdvanceRequest();
        request.ApplyPaymentMethodPolicy(thresholdNgn: 30_000m);
        request.TotalAmountNgn = 50_000m;
        request.ApplyPaymentMethodPolicy(thresholdNgn: 30_000m);

        request.TryGetField(nameof(CashAdvanceRequest.PaymentMethod), out var value)
            .Should().BeTrue();
        value.Should().Be(nameof(PaymentMethod.BankTransfer));
    }

    [Fact]
    public void CashAdvanceRequest_FieldNames_includes_PaymentMethod()
    {
        CashAdvanceRequest.FieldNames.Should().Contain(nameof(CashAdvanceRequest.PaymentMethod));
    }
}
