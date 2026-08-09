using System.Collections.Concurrent;
using System.Text.Json;
using Desicon.Workflow.Core.Definitions;

namespace Desicon.Workflow.Infrastructure.Workflow;

/// <summary>
/// Loads every *.workflow.json file in a directory (see modules/ at the repo
/// root) and indexes the parsed definitions by (ModuleKey, Version).
/// Definitions are loaded once and cached for the process lifetime -- a new
/// module version is a new deployment, not a hot reload, matching
/// WorkflowDefinition's own "data, not code" contract.
///
/// VERSIONS COEXIST
/// ----------------
/// Several files may declare the same ModuleKey with different Versions, and
/// all of them stay loaded. Requests are pinned to the version they were
/// raised under, so publishing a new version must not disturb work already in
/// flight; that only holds if the older definition is still resolvable.
///
/// The index was previously keyed on ModuleKey alone, which meant two files
/// for one module silently overwrote each other in directory-enumeration
/// order -- no error, and which one survived depended on the filesystem.
/// </summary>
public sealed class JsonWorkflowDefinitionProvider : IWorkflowDefinitionProvider, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;
    private readonly ConcurrentDictionary<(string ModuleKey, int Version), WorkflowDefinition> _cache = new();
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

        // Highest version already in force. A version dated in the future is
        // published but not yet live, so a request raised today must not be
        // raised under it -- same rule GetPolicyValue applies to policy rows.
        var now = DateTimeOffset.UtcNow;

        var current = _cache
            .Where(e => string.Equals(e.Key.ModuleKey, moduleKey, StringComparison.Ordinal)
                        && e.Value.EffectiveFrom <= now)
            .OrderByDescending(e => e.Key.Version)
            .Select(e => e.Value)
            .FirstOrDefault();

        if (current is null)
        {
            var published = _cache.Keys
                .Where(k => string.Equals(k.ModuleKey, moduleKey, StringComparison.Ordinal))
                .Select(k => k.Version)
                .OrderBy(v => v)
                .ToList();

            throw new InvalidOperationException(published.Count == 0
                ? $"No workflow definition is published for module '{moduleKey}'."
                : $"Module '{moduleKey}' has versions {string.Join(", ", published)} published, but none is yet effective as at {now:u}.");
        }

        return current;
    }

    public async Task<WorkflowDefinition> GetAsync(
        string moduleKey, int version, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);

        if (_cache.TryGetValue((moduleKey, version), out var definition))
        {
            return definition;
        }

        // Loud on purpose. A request pinned to a version nobody publishes any
        // more cannot be evaluated, and the one thing that must not happen is
        // quietly evaluating it against a different version instead -- that is
        // the behaviour this whole mechanism exists to remove.
        var published = _cache.Keys
            .Where(k => string.Equals(k.ModuleKey, moduleKey, StringComparison.Ordinal))
            .Select(k => k.Version)
            .OrderBy(v => v)
            .ToList();

        throw new InvalidOperationException(
            $"Module '{moduleKey}' version {version} is not published. " +
            (published.Count == 0
                ? "No versions of this module are published at all."
                : $"Published versions: {string.Join(", ", published)}. ") +
            "Requests raised under a version must keep being evaluated against it, so removing a definition file strands every request still open under it.");
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _cache.Values.ToList();
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

                var key = (definition.ModuleKey, definition.Version);

                if (_cache.TryGetValue(key, out var existing))
                {
                    throw new InvalidOperationException(
                        $"Two definition files declare module '{definition.ModuleKey}' version {definition.Version}. " +
                        $"A version identifies a process exactly once; '{path}' collides with an already-loaded definition " +
                        $"('{existing.DisplayName}'). Bump the version on whichever is the newer process.");
                }

                _cache[key] = definition;
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
