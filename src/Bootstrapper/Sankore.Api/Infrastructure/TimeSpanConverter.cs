namespace Sankore.Api.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Handles TimeSpan serialization as "HH:mm:ss" or "d.HH:mm:ss" strings.
/// System.Text.Json has no built-in TimeSpan converter for JSON strings.
/// </summary>
public sealed class TimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        if (string.IsNullOrWhiteSpace(str))
            return TimeSpan.Zero;

        return TimeSpan.TryParse(str, out var ts)
            ? ts
            : TimeSpan.Zero;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
