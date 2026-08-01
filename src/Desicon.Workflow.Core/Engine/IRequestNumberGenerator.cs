using Desicon.Workflow.Core.Definitions;

namespace Desicon.Workflow.Core.Engine;

/// <summary>
/// Mints the human-facing request number (e.g. EXP-2026-000123) at
/// submission. Implemented outside this project because the "no gaps from
/// failed inserts being reused" guarantee (docs/03-Data-Model-ERD.md section
/// 3) needs a real database sequence, not an in-memory counter -- see
/// SqlSequenceRequestNumberGenerator in Infrastructure.
/// </summary>
public interface IRequestNumberGenerator
{
    Task<string> GenerateAsync(
        WorkflowDefinition definition, DateTimeOffset now, CancellationToken cancellationToken = default);
}
