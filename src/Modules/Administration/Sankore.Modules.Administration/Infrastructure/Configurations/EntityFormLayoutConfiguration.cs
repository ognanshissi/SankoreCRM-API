namespace Sankore.Modules.Administration.Infrastructure.Configurations;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Administration.Domain.Forms;

internal sealed class EntityFormLayoutConfiguration : IEntityTypeConfiguration<EntityFormLayout>
{
    public void Configure(EntityTypeBuilder<EntityFormLayout> builder)
    {
        builder.ToTable("entity_form_layouts");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Entity).HasConversion<string>().HasMaxLength(20);

        // Same arrangement as beneficiary_policy, and for the same two reasons: `'{}'::jsonb`
        // because EF would emit `DEFAULT ''`, which PostgreSQL refuses on a jsonb column; and an
        // explicit comparer because the document holds lists, which record equality does not
        // compare — moving one field would leave the entity looking unchanged and the save would
        // be dropped.
        builder.Property(x => x.Layout)
            .HasColumnType("jsonb")
            .HasColumnName("layout")
            .HasConversion(new FormLayoutDocumentConverter())
            .HasDefaultValueSql("'{}'::jsonb")
            .Metadata.SetValueComparer(new FormLayoutDocumentComparer());

        // One layout per entity and tenant.
        builder.HasIndex(x => new { x.TenantId, x.Entity }).IsUnique();
    }
}

internal sealed class FormLayoutDocumentConverter()
    : ValueConverter<FormLayoutDocument, string>(
        layout => Serialize(layout),
        json => Deserialize(json))
{
    /// <summary>Enums as names, so the column reads in psql and survives an enum being reordered.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static string Serialize(FormLayoutDocument layout) => JsonSerializer.Serialize(layout, Options);

    internal static FormLayoutDocument Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return FormLayoutDocument.Empty;

        try
        {
            var layout = JsonSerializer.Deserialize<FormLayoutDocument>(json, Options);
            return layout is null
                ? FormLayoutDocument.Empty
                : new FormLayoutDocument(layout.Sections ?? [], layout.Fields ?? []);
        }
        catch (JsonException)
        {
            return FormLayoutDocument.Empty;
        }
    }
}

internal sealed class FormLayoutDocumentComparer()
    : ValueComparer<FormLayoutDocument>(
        (left, right) => Equal(left, right),
        layout => FormLayoutDocumentConverter.Serialize(layout).GetHashCode(StringComparison.Ordinal),
        layout => FormLayoutDocumentConverter.Deserialize(FormLayoutDocumentConverter.Serialize(layout)))
{
    private static bool Equal(FormLayoutDocument? left, FormLayoutDocument? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return string.Equals(
            FormLayoutDocumentConverter.Serialize(left),
            FormLayoutDocumentConverter.Serialize(right),
            StringComparison.Ordinal);
    }
}
