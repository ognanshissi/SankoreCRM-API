using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class BeneficialOwnerConfiguration : IEntityTypeConfiguration<BeneficialOwner>
{
    public void Configure(EntityTypeBuilder<BeneficialOwner> builder)
    {
        builder.ToTable("beneficial_owners");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.ControlType).HasConversion<string>().HasMaxLength(30).IsRequired();

        // AML thresholds are expressed in whole percents with two decimals
        // (e.g. 33.33) — decimal(5,2) covers 0.00 … 100.00 exactly.
        builder.Property(o => o.OwnershipPercentage).HasColumnType("decimal(5,2)");

        builder.Property(o => o.ExternalFullName).HasMaxLength(150);
        builder.Property(o => o.ExternalNationality).HasMaxLength(100);
        builder.Property(o => o.EncryptedExternalDocumentNumber).HasMaxLength(2000);
        builder.Property(o => o.ExternalDocumentBlindIndex).HasMaxLength(64);

        builder.HasIndex(o => new { o.TenantId, o.LegalClientId });
        builder.HasIndex(o => new { o.TenantId, o.LinkedClientId });

        // Same schema, so a real FK is fine here — Restrict, because deleting a
        // client that is someone's beneficial owner must fail loudly.
        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(o => o.LinkedClientId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(o => o.IsActive);
    }
}
