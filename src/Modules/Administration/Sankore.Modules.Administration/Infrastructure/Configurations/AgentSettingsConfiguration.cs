using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class AgentSettingsConfiguration : IEntityTypeConfiguration<AgentSettings>
{
    public void Configure(EntityTypeBuilder<AgentSettings> b)
    {
        b.ToTable("agent_settings");
        b.HasKey(s => s.UserId);
        b.Property(s => s.UserId).ValueGeneratedNever();
        b.HasIndex(s => s.TenantId);

        b.Property(s => s.Availability).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(AgentAvailability.Available).ValueGeneratedNever();
        b.Property(s => s.TransferTasksToReplacement).HasDefaultValue(false).ValueGeneratedNever();
        b.Property(s => s.CapacityCeiling)
            .HasDefaultValue(AgentSettings.DefaultCapacityCeiling).ValueGeneratedNever();

        // jsonb typé : EF génère un DEFAULT '' pour une propriété convertie non nullable, que
        // PostgreSQL refuse sur du jsonb — d'où le défaut SQL explicite (cf. BeneficiaryPolicy).
        b.Property(s => s.WorkingHours)
            .HasConversion(new JsonListConverter<WorkingDay>(), new JsonListComparer<WorkingDay>())
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");
        b.Property(s => s.NotificationPreferences)
            .HasConversion(new JsonListConverter<NotificationPreference>(), new JsonListComparer<NotificationPreference>())
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'[]'::jsonb");
    }
}

/// <summary>Une liste d'enregistrements en jsonb. Une ligne illisible se lit comme une liste vide.</summary>
internal sealed class JsonListConverter<T>()
    : ValueConverter<List<T>, string>(
        list => JsonSerializer.Serialize(list, JsonListOptions.Web),
        json => Deserialize(json))
{
    private static List<T> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, JsonListOptions.Web) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Sans comparateur, EF compare les listes par référence : remplacer un élément par un autre de
/// même valeur passerait, mais modifier une liste en place serait ignoré. Comparer la forme
/// sérialisée suffit et reste juste.
/// </summary>
internal sealed class JsonListComparer<T>()
    : ValueComparer<List<T>>(
        (left, right) => Serialize(left) == Serialize(right),
        list => Serialize(list).GetHashCode(StringComparison.Ordinal),
        list => JsonSerializer.Deserialize<List<T>>(Serialize(list), JsonListOptions.Web) ?? new List<T>())
{
    private static string Serialize(List<T>? list) => JsonSerializer.Serialize(list ?? [], JsonListOptions.Web);
}

internal static class JsonListOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
