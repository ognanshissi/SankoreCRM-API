namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

internal sealed class MsgWebhookEventConfiguration : IEntityTypeConfiguration<MsgWebhookEvent>
{
    public void Configure(EntityTypeBuilder<MsgWebhookEvent> b)
    {
        b.ToTable("msg_webhook_event");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).ValueGeneratedNever();

        b.Property(e => e.DedupKey).HasMaxLength(64).IsRequired();
        b.Property(e => e.Outcome).HasMaxLength(200);

        // Une redélivrance identique ne fait aucun travail : l'insertion EST le contrôle.
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.DedupKey })
            .IsUnique()
            .HasDatabaseName("ux_msg_webhook_event_dedup");

        // Ce qui reste à traiter, pour l'exploitant qui cherche une ligne bloquée.
        b.HasIndex(e => new { e.TenantId, e.ProcessedAt, e.ReceivedAt })
            .HasDatabaseName("ix_msg_webhook_event_pending");

        b.Ignore(e => e.DomainEvents);
    }
}
