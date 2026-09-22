namespace Sankore.Api.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Handles empty strings and "null" as null for Guid? properties.
/// Without this, System.Text.Json throws when the client sends "agencyId": "".
/// </summary>
public sealed class NullableGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;

            return Guid.TryParse(str, out var guid) ? guid : null;
        }

        return reader.GetGuid();
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
