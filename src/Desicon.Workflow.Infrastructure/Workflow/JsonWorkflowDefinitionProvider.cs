using System.Collections.Concurrent;
using System.Text.Json;
using Desicon.Workflow.Core.Definitions;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Loads every *.workflow.json file in a directory (see modules/ at the repo
/// root) and indexes the parsed definitions by ModuleKey. Definitions are
/// loaded once and cached for the process lifetime -- a new module version is
/// a new deployment, not a hot reload, matching WorkflowDefinition's own
/// "data, not code" contract.
/// </summary>
public sealed class JsonWorkflowDefinitionProvider : IWorkflowDefinitionProvider, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<string, WorkflowDefinition> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _loaded;

    public JsonWorkflowDefinitionProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public async Task<WorkflowDefinition> GetAsync(string moduleKey, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (!_cache.TryGetValue(moduleKey, out var definition))
        {
            throw new InvalidOperationException(
                $"No workflow definition is published for module '{moduleKey}'.");
        }

        return definition;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(_directory, "*.workflow.json"))
            {
                await using var stream = File.OpenRead(path);

                var definition = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(
                    stream, SerializerOptions, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"Workflow definition file '{path}' deserialised to null.");

                _cache[definition.ModuleKey] = definition;
            }

            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public void Dispose() => _loadLock.Dispose();
}
