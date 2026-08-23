namespace Sankore.Modules.Notifications.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Notifications.Domain;

internal sealed class EmailDeliveryLogConfiguration
    : IEntityTypeConfiguration<EmailDeliveryLog>
{
    public void Configure(EntityTypeBuilder<EmailDeliveryLog> b)
    {
        b.ToTable("email_delivery_logs");
        b.HasKey(l => l.Id);

        b.Property(l => l.RecipientEmail).HasMaxLength(256).IsRequired();
        b.Property(l => l.EventType).HasConversion<string>().HasMaxLength(20);

        b.HasIndex(l => new { l.TenantId, l.RecordedAt });
        b.HasIndex(l => l.OutboxMessageId);
    }
}
