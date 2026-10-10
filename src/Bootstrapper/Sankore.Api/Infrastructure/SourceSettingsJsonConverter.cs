namespace Sankore.Api.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Custom converter for <see cref="SourceSettings"/> that handles polymorphic
/// serialization/deserialization manually (no [JsonPolymorphic] attributes).
/// Accepts JSON with or without $mode — infers the concrete type from properties if missing.
/// </summary>
public sealed class SourceSettingsJsonConverter : JsonConverter<SourceSettings>
{
    // CamelCase, matching the OpenAPI document and the EF-side SourceSettingsConverter. Without
    // the policy the WRITE side emitted the record's own PascalCase against a contract promising
    // camelCase, so every mode-specific field of a GET read back undefined in the generated
    // client; the case-insensitive flag already covered the inbound direction.
    private static readonly JsonSerializerOptions InnerOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Discriminator value to concrete record. The single host-side source of that mapping: both
    /// <see cref="ResolveType"/> and <c>SourceSettingsSchemaFilter</c> read it, so the mode a
    /// client may SEND and the mode the OpenAPI document ADVERTISES cannot drift apart — which is
    /// how this hierarchy came to be resolved by a discriminator the contract never mentioned.
    ///
    /// <para>
    /// Compared case-INSENSITIVELY. The switch this replaced was an ordinal string switch, and a
    /// PRESENT-but-mis-cased <c>$mode</c> is not inferred — <see cref="Read"/> only infers when
    /// the property is absent — so <c>"$mode": "embeddedScript"</c> resolved to nothing and the
    /// whole settings object came back null, while <c>"mode": "embeddedScript"</c> on the sibling
    /// field bound fine through <see cref="JsonStringEnumConverter"/>.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> SettingsTypesByMode =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(IntegrationMode.EmbeddedScript)] = typeof(EmbeddedScriptSettings),
            [nameof(IntegrationMode.ServerWebhook)] = typeof(ServerWebhookSettings),
            [nameof(IntegrationMode.ScheduledPull)] = typeof(ScheduledPullSettings),
            [nameof(IntegrationMode.PlatformConnection)] = typeof(PlatformSettings),
            [nameof(IntegrationMode.SocialTracking)] = typeof(SocialTrackingSettings),
            [nameof(IntegrationMode.Internal)] = typeof(InternalSettings),
        };

    public override SourceSettings? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Accept the v1 dictionary mapping from older clients
        var rawText = SourceSettingsUpgrader.MigrateJson(root.GetRawText());

        // Resolve mode: from $mode property or inferred from structure
        string mode;
        if (root.TryGetProperty("$mode", out var modeEl) && modeEl.GetString() is not null)
            mode = modeEl.GetString()!;
        else
            mode = InferMode(root);

        var type = ResolveType(mode);
        if (type is null) return null;

        return (SourceSettings?)JsonSerializer.Deserialize(rawText, type, InnerOpts);
    }

    public override void Write(
        Utf8JsonWriter writer, SourceSettings value, JsonSerializerOptions options)
    {
        // Write as concrete type with $mode injected
        writer.WriteStartObject();
        writer.WriteString("$mode", value.ExpectedMode.ToString());

        // Serialize concrete type to a temporary doc, then copy all properties
        var json = JsonSerializer.Serialize(value, value.GetType(), InnerOpts);
        using var innerDoc = JsonDocument.Parse(json);
        foreach (var prop in innerDoc.RootElement.EnumerateObject())
        {
            prop.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Last resort when the body carries no <c>$mode</c>.
    ///
    /// <para>
    /// Both shapes are recognised, and that is not belt-and-braces: v3 moved every flat marker
    /// this used to look for into a nested block (<c>script</c>, <c>pull</c>, …), so the v2 list
    /// on its own would match nothing a current client sends and fall through to
    /// <c>Internal</c> — quietly turning a web-form or provider-API body into an
    /// <c>InternalSettings</c> with none of its fields. The v2 markers stay because this reads
    /// the body BEFORE the upgrader has renamed anything.
    /// </para>
    ///
    /// <para>
    /// Inference remains a fallback and nothing more: <c>SourceSettingsSchemaFilter</c>
    /// publishes <c>$mode</c> as required precisely so no generated client depends on it.
    /// </para>
    /// </summary>
    private static string InferMode(JsonElement root)
    {
        // v3 — the blocks the settings editor writes.
        if (HasProp(root, "script") || HasProp(root, "hostedForm"))
            return "EmbeddedScript";
        if (HasProp(root, "pull"))
            return "ScheduledPull";
        if (HasProp(root, "allowedIps"))
            return "ServerWebhook";

        // v2 — flat, as rows written before the blocks existed still arrive.
        if (HasProp(root, "allowedOrigins") || HasProp(root, "formContainerId"))
            return "EmbeddedScript";
        if (HasProp(root, "signatureAlgorithm") || HasProp(root, "signatureHeaderName") || HasProp(root, "allowedIpAddresses"))
            return "ServerWebhook";
        if (HasProp(root, "endpointUrl") || HasProp(root, "cronSchedule") || HasProp(root, "authType"))
            return "ScheduledPull";
        if (HasProp(root, "platformName") && (HasProp(root, "pageId") || HasProp(root, "formId")))
            return "PlatformConnection";
        if (HasProp(root, "trackedKeywords"))
            return "SocialTracking";

        return "Internal";
    }

    private static bool HasProp(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static Type? ResolveType(string mode)
        => SettingsTypesByMode.TryGetValue(mode, out var type) ? type : null;
}
