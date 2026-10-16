using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadActivityConfiguration : IEntityTypeConfiguration<LeadActivity>
{
    public void Configure(EntityTypeBuilder<LeadActivity> builder)
    {
        builder.ToTable("lead_activities");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Subject).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Notes).HasMaxLength(2000);

        builder.Property(a => a.Type)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(a => a.Outcome)
            .HasConversion<string>()
            .HasMaxLength(30);

        // Attachments — JSONB references to external object storage (US-M13-100)
        builder.Property(a => a.AttachmentsJson)
            .HasColumnType("jsonb")
            .HasColumnName("attachments");

        // CTI (US-M13-101)
        builder.Property(a => a.CtiCallReference).HasMaxLength(200);

        // UNIQUE per tenant, and the uniqueness is the idempotency of TEL-B-11.
        //
        // The telephony module publishes a wrap-up event through an outbox with at-least-once
        // delivery, and an agent may legitimately wrap the same call up twice to correct an
        // outcome. Without this index both produce a second activity, so a lead's history shows
        // one conversation as two — and the agent performance figures that count activities show
        // it as two as well. The consumer's read is the friendly answer; this is the guarantee.
        //
        // FILTERED, because the column is null for every activity that is not a call and
        // PostgreSQL treats each null as distinct only in a non-unique index — a plain unique
        // index over a nullable column is fine in PostgreSQL, but the filter also keeps the index
        // the size of the calls rather than of the whole history.
        builder.HasIndex(a => new { a.TenantId, a.CtiCallReference })
            .IsUnique()
            .HasFilter("cti_call_reference IS NOT NULL")
            .HasDatabaseName("ux_lead_activity_cti_call");

        // Visit location — owned GeoPoint (US-M13-102)
        builder.OwnsOne(a => a.VisitLocation, loc =>
        {
            loc.Property(p => p.Latitude).HasColumnName("visit_lat");
            loc.Property(p => p.Longitude).HasColumnName("visit_lng");
        });

        builder.Property(a => a.VisitPhotoReference).HasMaxLength(500);

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.LeadId, a.PerformedAt });
        builder.HasIndex(a => new { a.TenantId, a.LeadId });
    }
}
