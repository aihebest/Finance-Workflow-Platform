using System.Text.Json;
using Desicon.Workflow.Domain.Requests;

var outputPath = args.Length > 0 ? args[0] : "guard-field-schema.json";
var fullPath = Path.GetFullPath(outputPath);

var schema = new RequestGuardFieldSchema();

// Sorted so the emitted file diffs cleanly instead of reordering on every
// export.
var document = RequestGuardFieldSchema.ModuleKeys
    .OrderBy(key => key, StringComparer.Ordinal)
    .ToDictionary(
        key => key,
        key => schema.GetFieldNames(key).OrderBy(name => name, StringComparer.Ordinal).ToArray());

var json = JsonSerializer.Serialize(document, SchemaJson.Options);

var directory = Path.GetDirectoryName(fullPath);
if (!string.IsNullOrEmpty(directory))
{
    Directory.CreateDirectory(directory);
}

File.WriteAllText(fullPath, json + Environment.NewLine);

Console.WriteLine($"Guard field schema written to {fullPath}");

internal static class SchemaJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
}
