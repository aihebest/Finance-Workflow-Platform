using Desicon.Workflow.Core.Definitions;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Definitions;

/// <summary>
/// Covers GetPolicyValue's effective-dating lookup: a policy figure is a
/// dated row in the module's own PolicyValues table, not a constant, so a
/// future change ships as an additional row rather than an edit to the
/// existing one (see WorkflowDefinitionExtensions.GetPolicyValue).
/// </summary>
public sealed class WorkflowDefinitionExtensionsTests
{
    private static WorkflowDefinition DefinitionWithPolicyValues(params PolicyValue[] policyValues) => new()
    {
        ModuleKey = "TEST_MODULE",
        DisplayName = "Test Module",
        FormCode = "TEST-000",
        FormRevision = "01",
        NumberFormat = "TST-{yyyy}-{seq:000000}",
        PolicyValues = policyValues,
        States =
        [
            new WorkflowState { Key = "Start", Label = "Start", Type = StateType.Initial },
            new WorkflowState { Key = "End", Label = "End", Type = StateType.Terminal }
        ],
        Transitions = []
    };

    [Fact]
    public void Single_always_effective_row_resolves_regardless_of_asOf()
    {
        var definition = DefinitionWithPolicyValues(
            new PolicyValue { Key = "THRESHOLD", Value = 30_000m });

        definition.GetPolicyValue("THRESHOLD", DateTimeOffset.MinValue).Should().Be(30_000m);
        definition.GetPolicyValue("THRESHOLD", DateTimeOffset.UtcNow).Should().Be(30_000m);
    }

    [Fact]
    public void AsOf_before_the_later_rows_effective_date_returns_the_earlier_value()
    {
        var definition = DefinitionWithPolicyValues(
            new PolicyValue { Key = "THRESHOLD", Value = 30_000m, EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new PolicyValue { Key = "THRESHOLD", Value = 50_000m, EffectiveFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) });

        var asOf = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        definition.GetPolicyValue("THRESHOLD", asOf).Should().Be(30_000m);
    }

    [Fact]
    public void AsOf_on_or_after_the_later_rows_effective_date_returns_the_later_value()
    {
        var definition = DefinitionWithPolicyValues(
            new PolicyValue { Key = "THRESHOLD", Value = 30_000m, EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
            new PolicyValue { Key = "THRESHOLD", Value = 50_000m, EffectiveFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) });

        var onEffectiveDate = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var afterEffectiveDate = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        definition.GetPolicyValue("THRESHOLD", onEffectiveDate).Should().Be(50_000m);
        definition.GetPolicyValue("THRESHOLD", afterEffectiveDate).Should().Be(50_000m);
    }

    [Fact]
    public void AsOf_before_every_rows_effective_date_throws()
    {
        var definition = DefinitionWithPolicyValues(
            new PolicyValue { Key = "THRESHOLD", Value = 30_000m, EffectiveFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) });

        var asOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var act = () => definition.GetPolicyValue("THRESHOLD", asOf);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*THRESHOLD*effective as of*");
    }

    [Fact]
    public void Unknown_key_throws()
    {
        var definition = DefinitionWithPolicyValues(
            new PolicyValue { Key = "THRESHOLD", Value = 30_000m });

        var act = () => definition.GetPolicyValue("NOT_A_REAL_KEY", DateTimeOffset.UtcNow);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOT_A_REAL_KEY*");
    }
}
