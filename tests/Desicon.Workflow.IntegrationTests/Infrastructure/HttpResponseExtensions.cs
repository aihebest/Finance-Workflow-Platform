using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Desicon.Workflow.IntegrationTests.Infrastructure;

/// <summary>Thin assertion helpers around the two HTTP shapes every mutation
/// endpoint returns -- see ProblemResults.ToApiResult: 200/201 with a plain
/// success object, or an RFC 7807 problem body on failure.</summary>
public static class HttpResponseExtensions
{
    public static async Task<JsonElement> ShouldSucceedAsync(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue(
            "expected a success response but got {0} {1}: {2}",
            (int)response.StatusCode, response.StatusCode, body);

        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static async Task<JsonElement> ShouldFailAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expected, "response body was: {0}", body);

        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    public static Guid GetGuid(this JsonElement element, string property) =>
        element.GetProperty(property).GetGuid();

    public static string GetString(this JsonElement element, string property) =>
        element.GetProperty(property).GetString()!;
}
