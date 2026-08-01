using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desicon.Workflow.Core.Definitions;

/// <summary>
/// Allows a notification target to be written either as a single value or as
/// an array, so definition authors can write "to": "Requester" as well as
/// "to": ["Requester", "Beneficiary"].
/// </summary>
public sealed class StringOrArrayConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var single = reader.GetString();
            return single is null ? Array.Empty<string>() : new[] { single };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = new List<string>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException(
                        "Notification targets must be strings.");
                }

                var value = reader.GetString();

                if (value is not null)
                {
                    values.Add(value);
                }
            }

            return values;
        }

        throw new JsonException(
            "Notification target must be a string or an array of strings.");
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();

        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
