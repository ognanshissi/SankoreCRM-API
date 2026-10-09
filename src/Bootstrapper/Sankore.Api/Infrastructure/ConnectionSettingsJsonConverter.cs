namespace Sankore.Api.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Wire-format converter for the Integration module's polymorphic
/// <see cref="ConnectionSettings"/>, with a <c>$kind</c> discriminator.
///
/// <para>
/// It lives in the bootstrapper and not in the module for the same reason M13's
/// <c>SourceSettingsJsonConverter</c> does: the HTTP serializer is configured once, by the host,
/// in <c>ConfigureHttpJsonOptions</c>, and a module may not reference a host. The EF-side twin
/// is <c>ConnectionSettingsConverter</c>, inside the module; the two are deliberately separate
/// because they answer different questions — what the database stores, and what a client may
/// send.
/// </para>
///
/// <para>
/// <c>$kind</c> is REQUIRED on the way in, unlike M13's converter which infers a missing
/// discriminator from the shape of the payload. Inference is wrong here: several kinds share the
/// batch-capable base and would be indistinguishable by their properties, so a silently guessed
/// kind would store Amplitude settings on a Perfect Vision connection. An absent or unknown
/// <c>$kind</c> returns null, which the validators then refuse with a message naming the field.
/// </para>
/// </summary>
public sealed class ConnectionSettingsJsonConverter : JsonConverter<ConnectionSettings>
{
    private static readonly JsonSerializerOptions InnerOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public override ConnectionSettings? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$kind", out var kindEl) || kindEl.GetString() is not { } kind)
            return null;

        var type = ResolveType(kind);
        if (type is null) return null;

        return (ConnectionSettings?)JsonSerializer.Deserialize(root.GetRawText(), type, InnerOpts);
    }

    public override void Write(
        Utf8JsonWriter writer, ConnectionSettings value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("$kind", value.ExpectedKind.ToString());

        // Serialised against the CONCRETE type: the static type would emit only the base record's
        // four knobs and drop everything the kind actually needs.
        var json = JsonSerializer.Serialize(value, value.GetType(), InnerOpts);
        using var innerDoc = JsonDocument.Parse(json);

        foreach (var prop in innerDoc.RootElement.EnumerateObject())
            prop.WriteTo(writer);

        writer.WriteEndObject();
    }

    /// <summary>
    /// One switch, mirroring the module-side <c>ConnectionSettingsConverter.ResolveType</c>. The
    /// duplication is the known wart of this pattern in the repo; keeping the two literal and
    /// adjacent is better than a reflection scan that fails at run time on a renamed record.
    /// </summary>
    private static Type? ResolveType(string kind) => kind switch
    {
        nameof(IntegrationKind.Temenos) => typeof(TemenosSettings),
        nameof(IntegrationKind.Amplitude) => typeof(AmplitudeSettings),
        nameof(IntegrationKind.Sab) => typeof(SabSettings),
        nameof(IntegrationKind.PerfectVision) => typeof(PerfectVisionSettings),
        nameof(IntegrationKind.Orass) => typeof(OrassSettings),
        nameof(IntegrationKind.Fake) => typeof(FakeSettings),
        _ => null,
    };
}
