using Desicon.Workflow.Core.Definitions;
using Desicon.Workflow.Core.Guards;
using Desicon.Workflow.Core.Validation;
using Desicon.Workflow.Domain.Requests;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Validation;

/// <summary>
/// Covers the guard-field reachability check specifically: every guard's AST
/// is walked and every field reference must resolve against the module's
/// IGuardFieldSchema, the same schema tools/validate-definitions.mjs mirrors
/// from the emitted JSON file.
/// </summary>
public sealed class WorkflowDefinitionValidatorTests
{
    private sealed class FakeFieldSchema : IGuardFieldSchema
    {
        private readonly Dictionary<string, IReadOnlySet<string>> _fieldsByModule;

        public FakeFieldSchema(params string[] knownFields) =>
            _fieldsByModule = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
            {
                ["TEST_MODULE"] = knownFields.ToHashSet(StringComparer.Ordinal)
            };

        public IReadOnlySet<string> GetFieldNames(string moduleKey) =>
            _fieldsByModule.TryGetValue(moduleKey, out var names)
                ? names
                : throw new ArgumentException($"Unknown module key '{moduleKey}'.", nameof(moduleKey));
    }

    private static WorkflowDefinition DefinitionWithGuard(string? guard, string moduleKey = "TEST_MODULE") => new()
    {
        ModuleKey = moduleKey,
        DisplayName = "Test Module",
        FormCode = "TEST-000",
        FormRevision = "01",
        NumberFormat = "TST-{yyyy}-{seq:000000}",
        States =
        [
            new WorkflowState { Key = "Start", Label = "Start", Type = StateType.Initial },
            new WorkflowState { Key = "End", Label = "End", Type = StateType.Terminal }
        ],
        Transitions =
        [
            new WorkflowTransition
            {
                From = "Start",
                To = "End",
                Action = "APPROVE",
                Actor = new ActorSpec { Role = "FinanceManager" },
                Guard = guard,
                GuardMessage = guard is null ? null : "Blocked."
            }
        ]
    };

    [Fact]
    public void Guard_referencing_only_known_fields_raises_no_error()
    {
        var definition = DefinitionWithGuard("TotalAmountNgn > 0");
        var schema = new FakeFieldSchema("TotalAmountNgn");

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Should().NotContain(f => f.Code == "UNKNOWN_GUARD_FIELD");
    }

    [Fact]
    public void Guard_referencing_unknown_field_raises_error()
    {
        var definition = DefinitionWithGuard("NetPayableNGN >= 0");
        var schema = new FakeFieldSchema("NetPayableNgn");

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Should().ContainSingle(f =>
            f.Code == "UNKNOWN_GUARD_FIELD" && f.Message.Contains("NetPayableNGN"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Guard_with_multiple_unknown_fields_raises_one_error_per_field()
    {
        var definition = DefinitionWithGuard("Foo > 0 && Bar == 'x'");
        var schema = new FakeFieldSchema("TotalAmountNgn");

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Where(f => f.Code == "UNKNOWN_GUARD_FIELD").Should().HaveCount(2);
    }

    [Fact]
    public void Definition_with_no_guard_is_unaffected_by_the_check()
    {
        var definition = DefinitionWithGuard(null);
        var schema = new FakeFieldSchema();

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Should().NotContain(f => f.Code == "UNKNOWN_GUARD_FIELD");
    }

    [Fact]
    public void Unrecognised_module_key_skips_the_check_instead_of_raising_a_confusing_error()
    {
        var definition = DefinitionWithGuard("Whatever > 0", moduleKey: "NOT_REGISTERED");
        var schema = new FakeFieldSchema("TotalAmountNgn");

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Should().NotContain(f => f.Code == "UNKNOWN_GUARD_FIELD");
    }

    // ── Against the real RequestGuardFieldSchema, proving the two named bugs
    //    are actually fixed and not just fixed against a fake schema ────────

    [Fact]
    public void CashAdvance_guard_referencing_HasValidAllocation_and_HasOverdueAdvance_is_known()
    {
        var definition = DefinitionWithGuard(
            "HasValidAllocation && !HasOverdueAdvance", moduleKey: "CASH_ADVANCE");
        var schema = new RequestGuardFieldSchema();

        var result = WorkflowDefinitionValidator.Validate(definition, schema);

        result.Findings.Should().NotContain(f => f.Code == "UNKNOWN_GUARD_FIELD");
    }
}
