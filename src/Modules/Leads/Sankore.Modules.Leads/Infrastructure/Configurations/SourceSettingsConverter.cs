namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// EF Core ValueConverter: serializes <see cref="SourceSettings"/> to/from JSONB
/// with polymorphic $mode discriminator. Applies schema upgrades on read.
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
        => settings is null ? null : JsonSerializer.Serialize(settings, JsonOpts);

    private static SourceSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var settings = JsonSerializer.Deserialize<SourceSettings>(json, JsonOpts);
        if (settings is null) return null;

        // Apply schema upgrades transparently on read
        return SourceSettingsUpgrader.Upgrade(settings);
    }
}

/// <summary>
/// EF Core ValueComparer for <see cref="SourceSettings"/> — compares by serialized JSON
/// so that mutations to the settings object are detected by the change tracker.
/// </summary>
internal sealed class SourceSettingsComparer()
    : ValueComparer<SourceSettings?>(
        (a, b) => Serialize(a) == Serialize(b),
        v => (Serialize(v) ?? "").GetHashCode(),
        v => DeepClone(v))
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string? Serialize(SourceSettings? v)
        => v is null ? null : JsonSerializer.Serialize(v, JsonOpts);

    private static SourceSettings? DeepClone(SourceSettings? v)
    {
        if (v is null) return null;
        var json = JsonSerializer.Serialize(v, JsonOpts);
        return JsonSerializer.Deserialize<SourceSettings>(json, JsonOpts);
    }
}
