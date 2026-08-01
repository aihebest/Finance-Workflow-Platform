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
}
