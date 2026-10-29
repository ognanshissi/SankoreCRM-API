namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The `whk_` family: outbound webhooks. The signing secret is in the vault, never in a column.
/// </summary>
internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> b)
    {
        b.ToTable("whk_subscription");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.Name).HasMaxLength(WebhookSubscription.NameMaxLength).IsRequired();
        b.Property(s => s.Url).HasMaxLength(WebhookSubscription.UrlMaxLength).IsRequired();
        b.Property(s => s.Description).HasMaxLength(WebhookSubscription.DescriptionMaxLength);
        b.PrimitiveCollection(s => s.Events).IsRequired();
        b.Property(s => s.LastFailureCode).HasMaxLength(60);

        // The fan-out of lot 3 reads « the active subscriptions of this tenant » for every event.
        b.HasIndex(s => new { s.TenantId, s.IsActive });
        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> b)
    {
        b.ToTable("whk_delivery");
        b.HasKey(d => d.Id);
        b.Property(d => d.Id).ValueGeneratedNever();
        b.Property(d => d.EventCode).HasMaxLength(80).IsRequired();
        b.Property(d => d.Body).IsRequired();
        b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.LastFailureCode).HasMaxLength(60);
        b.Property(d => d.Version).IsRowVersion();

        // A redelivered event finds its row here and creates nothing.
        b.HasIndex(d => new { d.SubscriptionId, d.EventId }).IsUnique();
        // The delivery log of a subscription, newest first.
        b.HasIndex(d => new { d.SubscriptionId, d.CreatedAt });
        // The safety sweep: pending and overdue.
        b.HasIndex(d => new { d.Status, d.NextAttemptAt });
        b.HasIndex(d => new { d.TenantId, d.LeadId });
        b.HasIndex(d => new { d.TenantId, d.ClientId });

        // Deleting a subscription deletes its log.
        b.HasOne<WebhookSubscription>().WithMany().HasForeignKey(d => d.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(d => d.DomainEvents);
    }
}
