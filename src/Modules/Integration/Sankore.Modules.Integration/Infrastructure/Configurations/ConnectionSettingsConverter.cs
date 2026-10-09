namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// Stores <see cref="ConnectionSettings"/> as jsonb with a <c>$kind</c> discriminator, the same
/// shape M13 uses for its polymorphic source settings.
///
/// <para>
/// The discriminator is injected on write and read on load, rather than relying on
/// <c>[JsonPolymorphic]</c>: the records are plain and the mapping from discriminator to type
/// lives in one <see cref="ResolveType"/> switch, which is also what makes an unknown kind
/// degrade to <c>null</c> instead of throwing while loading a row written by a newer version.
/// </para>
/// </summary>
internal sealed class ConnectionSettingsConverter()
    : ValueConverter<ConnectionSettings?, string?>(v => Serialize(v), v => Deserialize(v))
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string? Serialize(ConnectionSettings? settings)
    {
        if (settings is null) return null;

        // Serialised against the CONCRETE type so every property of the subtype is written; the
        // static type would emit only the base's four knobs.
        var json = JsonSerializer.Serialize(settings, settings.GetType(), JsonOpts);
        return "{\"$kind\":\"" + settings.ExpectedKind + "\"," + json.TrimStart('{');
    }

    private static ConnectionSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("$kind", out var kindEl)) return null;

        var type = ResolveType(kindEl.GetString());
        if (type is null) return null;

        return (ConnectionSettings?)JsonSerializer.Deserialize(json, type, JsonOpts);
    }

    private static Type? ResolveType(string? kind) => kind switch
    {
        "Temenos" => typeof(TemenosSettings),
        "Amplitude" => typeof(AmplitudeSettings),
        "Sab" => typeof(SabSettings),
        "PerfectVision" => typeof(PerfectVisionSettings),
        "Orass" => typeof(OrassSettings),
        "Fake" => typeof(FakeSettings),
        _ => null,
    };
}

/// <summary>
/// Compares by serialised JSON.
///
/// <para>
/// Not optional: records are compared by value, but EF's change tracker needs a comparer for a
/// converted reference property or it never notices that the settings object was replaced — the
/// screen reports success and the connection keeps its old coordinates. M13's
/// <c>SourceSettingsComparer</c> exists for the same reason.
/// </para>
/// </summary>
internal sealed class ConnectionSettingsComparer()
    : ValueComparer<ConnectionSettings?>((a, b) => Equivalent(a, b), v => HashOf(v))
{
    private static bool Equivalent(ConnectionSettings? a, ConnectionSettings? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        return JsonSerializer.Serialize(a, a.GetType()) == JsonSerializer.Serialize(b, b.GetType());
    }

    // A static method and not an inline conditional: both lambdas are compiled into expression
    // trees, which cannot contain a pattern-matching `is`.
    private static int HashOf(ConnectionSettings? settings)
        => settings is null
            ? 0
            : JsonSerializer.Serialize(settings, settings.GetType()).GetHashCode(StringComparison.Ordinal);
}
