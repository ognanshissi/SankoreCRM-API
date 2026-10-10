namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Persistence;

/// <param name="encryptor">
/// M13's keyed encryptor, for the US TIN. Applied by hand from <see cref="LeadsDbContext"/> and
/// excluded from the assembly scan, like the other configurations that need a dependency.
/// </param>
internal sealed class FatcaDeclarationConfiguration(IFieldEncryptor encryptor)
    : IEntityTypeConfiguration<FatcaDeclaration>
{
    /// <summary>
    /// One ACTIVE declaration per person (BNF-B-11). Partial on <c>superseded_at IS NULL</c>, so
    /// the history is unbounded while the present is single — which is the whole point of
    /// superseding rather than editing.
    /// </summary>
    internal const string ActivePerSubjectIndex = "ux_fatca_declarations_active_per_subject";

    public void Configure(EntityTypeBuilder<FatcaDeclaration> builder)
    {
        builder.ToTable("fatca_declarations");
        builder.HasKey(d => d.Id);

        // The application supplies the key, so EF must not assume a set Guid means the row exists
        // — see LeadHouseholdMemberConfiguration for what that costs.
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.SubjectType).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(d => d.Documentation).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // The seven indices as a typed jsonb document — read as a set, never filtered on
        // individually. Converter plus comparer, like BeneficiaryPolicy: EF compares the converted
        // value, and a record whose equality is property-wise still needs the comparer for the
        // snapshot to be a real one.
        builder.Property(d => d.Indicia)
               .HasColumnType("jsonb")
               .HasColumnName("indicia")
               .HasConversion(new FatcaIndiciaConverter())
               .HasDefaultValueSql("'{}'::jsonb")
               .Metadata.SetValueComparer(new FatcaIndiciaComparer());

        // SENSITIVE. Encrypted, therefore uncomparable in SQL — and no blind index, because
        // nothing looks a person up by their US TIN.
        builder.Property(d => d.UsTin)
               .HasColumnName("us_tin_enc")
               .HasConversion(EncryptedConverters.Text(encryptor));

        // References into object storage, not the documents themselves.
        builder.Property(d => d.SelfCertificationDocRef).HasMaxLength(500);
        builder.Property(d => d.EvidenceDocRef).HasMaxLength(500);
        builder.Property(d => d.RefusalReason).HasMaxLength(1000);

        builder.HasOne<Lead>()
               .WithMany()
               .HasForeignKey(d => d.LeadId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(d => d.IsActive);
        builder.Ignore(d => d.IsDocumentationOutstanding);
        builder.Ignore(d => d.RequiresComplianceReview);

        builder.HasIndex(d => new { d.TenantId, d.LeadId });

        builder.HasIndex(d => new { d.LeadId, d.SubjectType, d.SubjectId })
               .IsUnique()
               .HasDatabaseName(ActivePerSubjectIndex)
               .HasFilter("superseded_at IS NULL");

        // Serves the compliance queue: the declarations somebody has to look at (BNF-B-11).
        // Partial — UsPerson and Recalcitrant are a small minority in this market.
        builder.HasIndex(d => new { d.TenantId, d.Status })
               .HasFilter("superseded_at IS NULL AND status IN ('UsPerson', 'Recalcitrant')");
    }
}

/// <summary>Maps <see cref="FatcaIndicia"/> to a typed <c>jsonb</c> column.</summary>
internal sealed class FatcaIndiciaConverter()
    : ValueConverter<FatcaIndicia, string>(
        indicia => JsonSerializer.Serialize(indicia, Options),
        json => Deserialize(json))
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A row that does not deserialise reads as <see cref="FatcaIndicia.None"/> rather than
    /// throwing — and <c>None</c> is the SAFE direction here only because the stored
    /// <c>Status</c> is what decides anything: a hand-edited indicia document cannot silently
    /// turn a <c>UsPerson</c> into a <c>NonUsPerson</c>.
    /// </summary>
    private static FatcaIndicia Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return FatcaIndicia.None;

        try
        {
            return JsonSerializer.Deserialize<FatcaIndicia>(json, Options) ?? FatcaIndicia.None;
        }
        catch (JsonException)
        {
            return FatcaIndicia.None;
        }
    }
}

internal sealed class FatcaIndiciaComparer()
    : ValueComparer<FatcaIndicia>(
        (left, right) => Equal(left, right),
        indicia => JsonSerializer.Serialize(indicia, FatcaIndiciaConverter.Options)
                       .GetHashCode(StringComparison.Ordinal),
        indicia => Clone(indicia))
{
    private static bool Equal(FatcaIndicia? left, FatcaIndicia? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        return string.Equals(
            JsonSerializer.Serialize(left, FatcaIndiciaConverter.Options),
            JsonSerializer.Serialize(right, FatcaIndiciaConverter.Options),
            StringComparison.Ordinal);
    }

    private static FatcaIndicia Clone(FatcaIndicia indicia)
        => JsonSerializer.Deserialize<FatcaIndicia>(
               JsonSerializer.Serialize(indicia, FatcaIndiciaConverter.Options),
               FatcaIndiciaConverter.Options)
           ?? FatcaIndicia.None;
}
