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
