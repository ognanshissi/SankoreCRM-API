using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadIngestionConfiguration: IEntityTypeConfiguration<LeadIngestion>
{
    public void Configure(EntityTypeBuilder<LeadIngestion> b)
    {
        b.ToTable("lead_ingestions");
        b.HasKey(i => i.Id);
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.RejectionReason).HasMaxLength(500);
        b.Property(i => i.ExternalId).HasMaxLength(200);
        b.Property(i => i.RawPayloadJson).HasColumnType("jsonb");
        // FK intra-schema to lead_source_runs (nullable)
        b.HasOne<LeadSourceRun>()
            .WithMany()
            .HasForeignKey(i => i.RunId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(i => new { i.TenantId, i.LeadId });
        b.HasIndex(i => new { i.TenantId, i.SourceId });
        b.HasIndex(i => i.RunId).HasFilter("run_id IS NOT NULL");
        // Idempotence: one ingestion per (tenant, source, external_id)
        b.HasIndex(i => new { i.TenantId, i.SourceId, i.ExternalId })
            .IsUnique()
            .HasFilter("external_id IS NOT NULL")
            .HasDatabaseName("ux_ingestion_idempotency");
    }
}