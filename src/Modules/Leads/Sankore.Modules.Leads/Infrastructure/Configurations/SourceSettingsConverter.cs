namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// EF Core ValueConverter: serializes <see cref="SourceSettings"/> to/from JSONB
/// with $mode discriminator. Applies schema upgrades on read.
/// Handles polymorphism manually (no [JsonPolymorphic] attributes on the domain).
/// </summary>
internal sealed class SourceSettingsConverter()
    : ValueConverter<SourceSettings?, string?>(
        v => Serialize(v),
        v => Deserialize(v))
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static string? Serialize(SourceSettings? settings)
    {
        if (settings is null) return null;
        // Serialize the concrete type to get all properties, then inject $mode
        var json = JsonSerializer.Serialize(settings, settings.GetType(), JsonOpts);
        var mode = settings.ExpectedMode.ToString();
        return "{\"$mode\":\"" + mode + "\"," + json.TrimStart('{');
    }

    private static SourceSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Read $mode discriminator
        string? mode = null;
        if (root.TryGetProperty("$mode", out var modeEl))
            mode = modeEl.GetString();

        if (mode is null) return null;

        var type = ResolveType(mode);
        if (type is null) return null;

        var settings = (SourceSettings?)JsonSerializer.Deserialize(json, type, JsonOpts);
        if (settings is null) return null;

        return SourceSettingsUpgrader.Upgrade(settings);
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

/// <summary>
/// EF Core ValueComparer for <see cref="SourceSettings"/> — compares by serialized JSON
/// so that mutations to the settings object are detected by the change tracker.
/// </summary>
internal sealed class SourceSettingsComparer()
    : ValueComparer<SourceSettings?>(
        (a, b) => Ser(a) == Ser(b),
        v => (Ser(v) ?? "").GetHashCode(),
        v => Clone(v))
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string? Ser(SourceSettings? v)
        => v is null ? null : JsonSerializer.Serialize(v, v.GetType(), JsonOpts);

    private static SourceSettings? Clone(SourceSettings? v)
    {
        if (v is null) return null;
        var json = JsonSerializer.Serialize(v, v.GetType(), JsonOpts);
        return (SourceSettings?)JsonSerializer.Deserialize(json, v.GetType(), JsonOpts);
    }
}
