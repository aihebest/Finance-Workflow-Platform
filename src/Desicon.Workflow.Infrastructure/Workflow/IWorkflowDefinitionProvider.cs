using Desicon.Workflow.Core.Definitions;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Resolves the published <see cref="WorkflowDefinition"/> for a module. A
/// workflow definition is data, not code (see WorkflowDefinition's own
/// remarks) -- this is the boundary where that data enters the running
/// system.
/// </summary>
public interface IWorkflowDefinitionProvider
{
    Task<WorkflowDefinition> GetAsync(string moduleKey, CancellationToken cancellationToken = default);

    /// <summary>Every published definition, for module discovery (GET /modules)
    /// and for building indexes such as InboxStateIndex.</summary>
    Task<IReadOnlyList<WorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
}
