using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class ComplianceSettingsConfiguration : IEntityTypeConfiguration<ComplianceSettings>
{
    public void Configure(EntityTypeBuilder<ComplianceSettings> b)
    {
        b.ToTable("compliance_settings");
        b.HasKey(s => s.Id);

        b.Property(s => s.TenantId).IsRequired();

        b.Property(s => s.FatcaEnabled).HasDefaultValue(false).IsRequired();

        // Enums as strings, the repo convention: a reordered enum must not silently re-label
        // every stored row.
        b.Property(s => s.FatcaRegime).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.FatcaRequiredAt).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Exactly 19 characters, fixed length — see ComplianceSettings.GiinIsWellFormed.
        b.Property(s => s.FatcaGiin).HasMaxLength(19).IsFixedLength();

        b.Property(s => s.FatcaForCoHolders).HasDefaultValue(true).IsRequired();
        b.Property(s => s.PepPostMandateMonths)
            .HasDefaultValue(ComplianceSettings.DefaultPepPostMandateMonths).IsRequired();

        b.Property(s => s.Version)
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One row per tenant.
        b.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName("ux_compliance_settings_tenant");
    }
}
