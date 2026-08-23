namespace Sankore.Modules.Notifications.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Notifications.Domain;

internal sealed class EmailOutboxMessageConfiguration
    : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> b)
    {
        b.ToTable("email_outbox_messages");
        b.HasKey(m => m.Id);

        b.Property(m => m.Module).HasMaxLength(50).IsRequired();
        b.Property(m => m.TemplateKey).HasMaxLength(100).IsRequired();
        b.Property(m => m.Locale).HasMaxLength(10).IsRequired();
        b.Property(m => m.RecipientEmail).HasMaxLength(256).IsRequired();
        b.Property(m => m.RecipientName).HasMaxLength(200);
        b.Property(m => m.IdempotencyKey).HasMaxLength(256).IsRequired();
        b.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.LastError).HasMaxLength(2000);

        b.HasIndex(m => m.IdempotencyKey).IsUnique();
        b.HasIndex(m => new { m.Status, m.CreatedAt });
        b.HasIndex(m => new { m.TenantId, m.Status });
    }
}
