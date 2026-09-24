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
    private static readonly JsonSerializerOptions InnerOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
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

    private static string InferMode(JsonElement root)
    {
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

    private static Type? ResolveType(string mode) => mode switch
    {
        "EmbeddedScript"     => typeof(EmbeddedScriptSettings),
        "ServerWebhook"      => typeof(ServerWebhookSettings),
        "ScheduledPull"      => typeof(ScheduledPullSettings),
        "PlatformConnection" => typeof(PlatformSettings),
        "SocialTracking"     => typeof(SocialTrackingSettings),
        "Internal"           => typeof(InternalSettings),
        _                    => null
    };
}
