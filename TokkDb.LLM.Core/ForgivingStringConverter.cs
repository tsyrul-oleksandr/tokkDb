using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokkDb.LLM.Core;

/// <summary>
/// Reads a JSON string tolerantly.
///
/// Models routinely send the wrong JSON type for a string field: a bare number,
/// a boolean, or - for a field that holds JSON text such as a schema definition -
/// an object or array rather than a quoted string. Without this the request
/// fails to deserialize before the tool ever runs, which produces an error the
/// model cannot act on.
///
/// Anything that is not already a string is converted to its textual form, and
/// objects and arrays keep their raw JSON so no information is lost.
/// </summary>
public sealed class ForgivingStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.False:
                return "false";

            case JsonTokenType.True:
                return "true";

            case JsonTokenType.Number:
                return Encoding.UTF8.GetString(
                    reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);

            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
            {
                // A field that carries JSON text was sent as real JSON. Keep it
                // verbatim rather than failing the whole call.
                using var document = JsonDocument.ParseValue(ref reader);
                return document.RootElement.GetRawText();
            }

            default:
                return reader.GetString();
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
