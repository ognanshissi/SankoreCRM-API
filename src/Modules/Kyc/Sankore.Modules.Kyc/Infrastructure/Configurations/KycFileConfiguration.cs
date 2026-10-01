namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycFileConfiguration : IEntityTypeConfiguration<KycFile>
{
    public void Configure(EntityTypeBuilder<KycFile> b)
    {
        b.ToTable("kyc_files");
        b.HasKey(f => f.Id);

        // Stored as text, like every other enum in this codebase: a value readable in psql is
        // worth more during a compliance audit than three bytes saved per row. Ordering is never
        // done on these columns — M13 learned what alphabetical priority ordering costs.
        b.Property(f => f.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(f => f.Tier).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(f => f.Channel).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(f => f.VigilanceLevel).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(f => f.ConfidenceLevel).HasConversion<string>().HasMaxLength(16);

        b.Property(f => f.Version).IsRowVersion();

        // One OPEN file per customer. Rejected and suspended files stay as evidence, so the
        // constraint is filtered rather than a plain unique index — and it is what makes
        // concurrent creation safe: the loser gets a unique violation, reported as
        // KYC_FILE_ALREADY_EXISTS instead of a second file nobody notices.
        b.HasIndex(f => new { f.TenantId, f.CustomerId })
            .IsUnique()
            .HasFilter("status NOT IN ('Rejected', 'Suspended')")
            .HasDatabaseName("ux_kyc_files_open_per_customer");

        // The daily review orchestrator scans on this pair.
        b.HasIndex(f => new { f.TenantId, f.NextReviewDate })
            .HasDatabaseName("ix_kyc_files_next_review");

        b.HasIndex(f => new { f.TenantId, f.Status })
            .HasDatabaseName("ix_kyc_files_status");

        b.Ignore(f => f.DomainEvents);
        b.Ignore(f => f.IsOpen);
    }
}
