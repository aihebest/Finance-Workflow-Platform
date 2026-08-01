using Desicon.Workflow.Core.Guards;

namespace Desicon.Workflow.Domain.Requests;

/// <summary>
/// Domain-side implementation of IGuardFieldSchema. Each module's field
/// surface is the union of the base Request fields and that module's own
/// request subclass fields -- exactly the two dictionaries TryGetField
/// dispatches through at runtime, so this can never see a field the
/// evaluator itself could not resolve.
/// </summary>
public sealed class RequestGuardFieldSchema : IGuardFieldSchema
{
    private static readonly Dictionary<string, IReadOnlySet<string>> FieldsByModule =
        new(StringComparer.Ordinal)
        {
            ["EXPENSE"] = Union(Request.FieldNames, ExpenseRequest.FieldNames),
            ["CASH_ADVANCE"] = Union(Request.FieldNames, CashAdvanceRequest.FieldNames),
        };

    /// <summary>Every module key this schema knows about. The single source
    /// consumers (e.g. the schema-export tool) use instead of hardcoding
    /// their own copy of the module list.</summary>
    public static IReadOnlyCollection<string> ModuleKeys => FieldsByModule.Keys;

    public IReadOnlySet<string> GetFieldNames(string moduleKey) =>
        FieldsByModule.TryGetValue(moduleKey, out var names)
            ? names
            : throw new ArgumentException($"Unknown module key '{moduleKey}'.", nameof(moduleKey));

    private static HashSet<string> Union(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Concat(b).ToHashSet(StringComparer.Ordinal);
}
