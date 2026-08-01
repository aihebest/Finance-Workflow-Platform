using Microsoft.Net.Http.Headers;

namespace Desicon.Workflow.Api.Http;

/// <summary>
/// ETag / If-Match mapped onto Request.RowVersion (see RequestConfiguration's
/// IsRowVersion() mapping). A stale write must come back as 412, never a
/// silent overwrite -- doc 05 §1's conventions are explicit on this.
/// </summary>
public static class ETagHelper
{
    public static string ToWeakETagValue(byte[] rowVersion) =>
        $"\"{Convert.ToBase64String(rowVersion)}\"";

    public static void SetETag(this HttpResponse response, byte[]? rowVersion)
    {
        if (rowVersion is { Length: > 0 })
        {
            response.Headers.ETag = ToWeakETagValue(rowVersion);
        }
    }

    /// <summary>Parses the If-Match header into a rowversion. Returns false
    /// (rather than throwing) for a missing or malformed header -- callers
    /// decide whether If-Match is required for their endpoint.</summary>
    public static bool TryGetIfMatchRowVersion(this HttpRequest request, out byte[] rowVersion)
    {
        rowVersion = Array.Empty<byte>();

        var header = request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParse(header, out var parsed) || parsed.Tag.Value is not { Length: > 0 } tag)
        {
            return false;
        }

        var trimmed = tag.Trim('"');

        try
        {
            rowVersion = Convert.FromBase64String(trimmed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
