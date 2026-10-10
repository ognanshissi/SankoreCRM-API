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
    // CamelCase, matching what the OpenAPI document publishes and what the EF-side
    // ConnectionSettingsConverter stores. Without the policy the WRITE side emitted the record's
    // own PascalCase — "BaseUrl" — against a contract promising "baseUrl", so every per-kind
    // field of a GET read back undefined in the generated client while reads kept working (the
    // case-insensitive flag below covers the inbound direction either way).
    private static readonly JsonSerializerOptions InnerOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Discriminator value to concrete record. The single host-side source of that mapping: both
    /// <see cref="ResolveType"/> and <c>ConnectionSettingsSchemaFilter</c> read it, so the kind a
    /// client may SEND and the kind the OpenAPI document ADVERTISES cannot drift apart — which is
    /// how the discriminator came to be required on the wire and absent from the contract.
    ///
    /// <para>
    /// Compared case-INSENSITIVELY. The switch this replaced was an ordinal string switch, so
    /// <c>"$kind": "temenos"</c> resolved to nothing and surfaced as "settings is required" on a
    /// correctly filled object — while <c>"kind": "temenos"</c> on the sibling field bound fine
    /// through <see cref="JsonStringEnumConverter"/>. One enum, two casing rules, no way to tell
    /// from the error message.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> SettingsTypesByKind =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(IntegrationKind.Temenos)] = typeof(TemenosSettings),
            [nameof(IntegrationKind.Amplitude)] = typeof(AmplitudeSettings),
            [nameof(IntegrationKind.Sab)] = typeof(SabSettings),
            [nameof(IntegrationKind.PerfectVision)] = typeof(PerfectVisionSettings),
            [nameof(IntegrationKind.Orass)] = typeof(OrassSettings),
            [nameof(IntegrationKind.Fake)] = typeof(FakeSettings),
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
    /// One table, mirroring the module-side <c>ConnectionSettingsConverter.ResolveType</c>. That
    /// duplication is the known wart of this pattern in the repo, and deliberate: the module may
    /// not reference a host, and two literal switches beat a reflection scan that fails at run
    /// time on a renamed record. What is NOT duplicated any more is the host's own copy — the
    /// schema filter reads <see cref="SettingsTypesByKind"/> rather than restating it.
    /// </summary>
    private static Type? ResolveType(string kind)
        => SettingsTypesByKind.TryGetValue(kind, out var type) ? type : null;
}
