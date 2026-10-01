namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycApprovalStepConfiguration : IEntityTypeConfiguration<KycApprovalStep>
{
    public void Configure(EntityTypeBuilder<KycApprovalStep> b)
    {
        b.ToTable("kyc_approval_steps");
        b.HasKey(s => s.Id);

        b.Property(s => s.Level).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(s => s.Decision).HasConversion<string>().HasMaxLength(24).IsRequired();
        b.Property(s => s.Comment).HasMaxLength(2000);

        // One row per level and per file: the circuit is created up-front, so a duplicate would
        // mean two people signing the same rung.
        b.HasIndex(s => new { s.KycFileId, s.Level })
            .IsUnique()
            .HasDatabaseName("ux_kyc_approval_steps_file_level");

        // The "which rung is waiting" lookup: the lowest rank still pending, per file. Decision
        // before rank because the predicate pins it to Pending and then takes a minimum.
        b.HasIndex(s => new { s.KycFileId, s.Decision, s.LevelRank })
            .HasDatabaseName("ix_kyc_approval_steps_pending_rank");

        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class KycReviewScheduleConfiguration : IEntityTypeConfiguration<KycReviewSchedule>
{
    public void Configure(EntityTypeBuilder<KycReviewSchedule> b)
    {
        b.ToTable("kyc_review_schedules");
        b.HasKey(r => r.Id);

        b.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(r => r.Reason).HasMaxLength(500);

        // The daily orchestrator scans exactly this: scheduled reviews of a tenant that have come
        // due. Without the composite it is a full scan of every review ever planned.
        b.HasIndex(r => new { r.TenantId, r.Status, r.DueDate })
            .HasDatabaseName("ix_kyc_review_schedules_due");

        b.HasIndex(r => r.KycFileId)
            .HasDatabaseName("ix_kyc_review_schedules_file");

        b.Ignore(r => r.DomainEvents);
    }
}
