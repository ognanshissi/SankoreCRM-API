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
        // DEFAULT TRUE at the column too, not only in the factory. The migration adds it to rows
        // that already exist, and a default of false would switch PEP vigilance off for every
        // institution in the database as a side effect of a deployment.
        b.Property(s => s.PepEnabled)
            .HasDefaultValue(true).IsRequired();

        b.Property(s => s.PepPostMandateMonths)
            .HasDefaultValue(ComplianceSettings.DefaultPepPostMandateMonths).IsRequired();

        // KRM-B-01. DEFAULT FALSE at the column as well as in the factory, and that direction is
        // the opposite of PepEnabled's on purpose: a deployment must never hand an existing
        // institution the right to validate a KYC file without a human. The ceiling and the sample
        // rate get their platform defaults so an officer who only flips the switch lands on the
        // recommended posture — capped at the simplified tier, 10 % reviewed afterwards.
        b.Property(s => s.KycAutoApprovalEnabled).HasDefaultValue(false).IsRequired();

        b.Property(s => s.KycAutoApprovalMaxTier)
            .HasMaxLength(16)
            .HasDefaultValue(KycRemotePolicyValues.SimplifiedTier)
            .IsRequired();

        b.Property(s => s.KycAutoApprovalSamplePct)
            .HasDefaultValue(ComplianceSettings.DefaultKycAutoApprovalSamplePct)
            .IsRequired();

        b.Property(s => s.Version)
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One row per tenant.
        b.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName("ux_compliance_settings_tenant");
    }
}
