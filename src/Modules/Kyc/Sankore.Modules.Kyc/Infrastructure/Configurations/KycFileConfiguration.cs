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

        // Same width as KycApprovalStep.Comment and KycDocument.RefusalReason — the three places an
        // operator's motive is stored — so the validator's cap and the column agree.
        b.Property(f => f.ManualValidationReason).HasMaxLength(2000);

        b.Property(f => f.Version).IsRowVersion();

        // KRM-B-02. Opaque references with no foreign key, like CustomerId and AgencyId: the
        // product line lives in M12's schema and the agent in M12's identity tables, and nothing
        // here joins across a schema boundary.
        //
        // NO index on either, deliberately. Neither is a predicate: the product is read with the
        // file in hand, and the responsible agent is an address a notification resolves, not a
        // filter — the list screens narrow by agency and status, which ix_kyc_files_agency_status
        // already covers. An index that no query uses is a write cost on the hottest table of the
        // module. If "my remote files" ever becomes a dashboard filter, that is when it earns one.
        b.Property(f => f.ProductLineId);
        b.Property(f => f.ResponsibleAgentId);

        // KRM-B-08. Text like every other enum here, and NULLABLE — null means "this file never
        // went through the remote decision policy", which is not the same statement as "a human
        // decides it". Almost every file in the table is in that state and always will be.
        b.Property(f => f.ValidationRoute).HasConversion<string>().HasMaxLength(16);

        // A code from KycRoutingReasons, not a sentence. 64 is generous for the longest of them
        // (QUALITY_RETRIES_EXCEEDED is 24) and leaves room for one more without a migration.
        b.Property(f => f.RoutingReason).HasMaxLength(64);
        b.Property(f => f.RoutedAt);

        // NO index, for the same reason ProductLineId has none: nothing filters on it today. The
        // compliance report KRM-B-11 will want — "the automatic approvals of this month" — reads
        // the Automatic approval STEPS, which ix_kyc_approval_steps_* already serves, not this
        // column.

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

        // The agency perimeter is a predicate on every list read, so it leads the index. Status
        // second because the dashboard filters on both and almost always narrows by status within a
        // perimeter, never the reverse. Rows with a null agency are indexed too: they are the ones
        // only an unrestricted caller sees, and PostgreSQL keeps nulls in a btree.
        b.HasIndex(f => new { f.TenantId, f.AgencyId, f.Status })
            .HasDatabaseName("ix_kyc_files_agency_status");

        b.Ignore(f => f.DomainEvents);
        b.Ignore(f => f.IsOpen);
    }
}
