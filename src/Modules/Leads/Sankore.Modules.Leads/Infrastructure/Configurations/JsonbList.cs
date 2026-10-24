namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A small list stored as one jsonb column, compared by content — the projects' checklist ticks
/// and circuit runs, a step's checklist. Same shape as the WhatsApp profiles' lists.
/// </summary>
internal static class JsonbList
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Configure<T>(PropertyBuilder<List<T>> property) => property
        .HasColumnType("jsonb")
        .HasDefaultValueSql("'[]'::jsonb")
        .HasConversion(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<T>>(v, Json) ?? new List<T>())
        .Metadata.SetValueComparer(new ValueComparer<List<T>>(
            (a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, Json), Json)!));
}
