namespace Desicon.Workflow.Core.Guards;

/// <summary>
/// The set of field names a guard expression may legally reference for a
/// given module. Core defines the contract but cannot implement it -- the
/// actual field names live on Domain's Request subclasses -- so this exists
/// purely to let WorkflowDefinitionValidator check a guard's AST against
/// Domain's field set without Core taking a dependency on Domain.
/// </summary>
public interface IGuardFieldSchema
{
    /// <summary>
    /// All field names <see cref="GuardEvaluator"/> can resolve for the given
    /// module key. Throws <see cref="ArgumentException"/> if the module key
    /// is not recognised.
    /// </summary>
    IReadOnlySet<string> GetFieldNames(string moduleKey);
}
