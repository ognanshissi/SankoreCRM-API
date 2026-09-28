using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientTimelineEntryConfiguration : IEntityTypeConfiguration<ClientTimelineEntry>
{
    public void Configure(EntityTypeBuilder<ClientTimelineEntry> builder)
    {
        builder.ToTable("client_timeline_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.SourceModule).HasMaxLength(50).IsRequired();
        builder.Property(e => e.EntryType).HasMaxLength(60).IsRequired();
        builder.Property(e => e.Summary).HasMaxLength(500).IsRequired();
        builder.Property(e => e.ReferenceType).HasMaxLength(60);
        builder.Property(e => e.ReferenceId).HasMaxLength(100);
        builder.Property(e => e.DedupKey).HasMaxLength(200).IsRequired();

        // The timeline is fed by at-least-once integration events: the dedup key
        // is the only thing standing between a redelivery and a duplicated entry.
        builder.HasIndex(e => new { e.TenantId, e.DedupKey })
            .IsUnique()
            .HasDatabaseName("ux_client_timeline_dedup");

        builder.HasIndex(e => new { e.TenantId, e.ClientId, e.OccurredAt });
    }
}
