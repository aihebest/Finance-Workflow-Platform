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
    /// <summary>
    /// The definition a NEW request should be raised under: the highest
    /// version whose EffectiveFrom has passed.
    /// </summary>
    /// <remarks>
    /// Correct only for creation. For a request that already exists, use
    /// <see cref="GetAsync(string, int, CancellationToken)"/> with the version
    /// stamped on it — see the remarks there for why.
    /// </remarks>
    Task<WorkflowDefinition> GetAsync(string moduleKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The exact version a request was raised under.
    /// </summary>
    /// <remarks>
    /// Every request carries <c>Request.DefinitionVersion</c>, stamped when it
    /// is created and never changed. It is evaluated against that version for
    /// the rest of its life, however many newer versions are published
    /// afterwards.
    ///
    /// Without this, changing a definition re-evaluates every in-flight request
    /// against the new one. A request sitting in a state the change removed
    /// then has no transitions out of it: TransitionsFrom returns an empty
    /// list, no error is raised anywhere, and it silently disappears from the
    /// worklist of the only people who could have acted on it while remaining
    /// open. That happened twice in dev on 8 August 2026 — see
    /// docs/14-Process-As-Described.md.
    ///
    /// Throws when the pinned version is not published. That is deliberate and
    /// is the whole point: falling back to the current version is precisely the
    /// silent behaviour this replaces. A request that cannot be evaluated must
    /// say so loudly, because the alternative is that it quietly stops existing.
    /// </remarks>
    Task<WorkflowDefinition> GetAsync(string moduleKey, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every published definition, all versions of every module.
    /// </summary>
    /// <remarks>
    /// All versions, not the current one per module, and InboxStateIndex
    /// depends on that: a request pinned to an older version can be sitting in
    /// a state only that version declares, and it still has to appear in
    /// somebody's worklist. Indexing only current versions would hide exactly
    /// the requests most at risk of being forgotten.
    ///
    /// Callers that want one row per module -- module discovery, for instance
    /// -- must reduce this themselves; see ModuleEndpoints.
    /// </remarks>
    Task<IReadOnlyList<WorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
}
