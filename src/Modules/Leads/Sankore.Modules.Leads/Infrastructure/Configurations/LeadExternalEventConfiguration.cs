using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadExternalEventConfiguration : IEntityTypeConfiguration<LeadExternalEvent>
{
    public void Configure(EntityTypeBuilder<LeadExternalEvent> b)
    {
        b.ToTable("lead_external_events");
        b.HasKey(e => e.Id);
        b.Property(e => e.ExternalEventId).HasMaxLength(LeadExternalEvent.EventIdMaxLength).IsRequired();
        b.Property(e => e.EventType).HasMaxLength(LeadExternalEvent.TypeMaxLength).IsRequired();
        b.Property(e => e.MatchedBy).HasMaxLength(20);
        b.Property(e => e.PayloadJson).HasColumnType("jsonb");
        b.Ignore(e => e.IsMatched);

        // A partner retrying the same event finds its row and starts nothing twice.
        b.HasIndex(e => new { e.TenantId, e.SourceId, e.ExternalEventId })
            .IsUnique()
            .HasDatabaseName("ux_lead_external_event_idempotency");
        b.HasIndex(e => new { e.TenantId, e.LeadId });
    }
}
