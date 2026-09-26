namespace Sankore.Modules.Leads.Features.Import;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Persists <see cref="ImportDefaults"/> on the job row as JSON.</summary>
internal static class ImportDefaultsSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string? Serialize(ImportDefaults? defaults)
        => defaults is null ? null : JsonSerializer.Serialize(defaults, Options);

    public static ImportDefaults Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ImportDefaults();

        try
        {
            return JsonSerializer.Deserialize<ImportDefaults>(json, Options) ?? new ImportDefaults();
        }
        catch (JsonException)
        {
            return new ImportDefaults();
        }
    }
}
