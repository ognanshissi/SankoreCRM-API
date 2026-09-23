namespace Sankore.Api.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Handles non-nullable Guid deserialization gracefully.
/// Treats empty strings and malformed values as Guid.Empty instead of throwing.
/// </summary>
public sealed class GuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return Guid.Empty;

            return Guid.TryParse(str, out var guid) ? guid : Guid.Empty;
        }

        return reader.GetGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
