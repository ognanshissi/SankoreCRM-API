using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        // The primary key IS the integration event id: inserting it is the
        // idempotency check. A redelivered event loses on the PK instead of being
        // processed twice (US-M01-BE-13). Never generate this value.
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.EventType).HasMaxLength(200).IsRequired();

        // Supports the retention sweep that trims processed ids after the broker's
        // redelivery window has safely elapsed.
        builder.HasIndex(m => new { m.TenantId, m.ReceivedAt });
    }
}
