namespace Sankore.Modules.Notifications.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Notifications.Domain;

internal sealed class SmsOutboxMessageConfiguration : IEntityTypeConfiguration<SmsOutboxMessage>
{
    public void Configure(EntityTypeBuilder<SmsOutboxMessage> b)
    {
        b.ToTable("sms_outbox_messages");
        b.HasKey(m => m.Id);

        b.Property(m => m.Module).HasMaxLength(50).IsRequired();
        b.Property(m => m.TemplateKey).HasMaxLength(100).IsRequired();
        b.Property(m => m.Locale).HasMaxLength(10).IsRequired();

        // E.164 caps at 15 digits plus the plus; 32 leaves room for the masked form that replaces
        // it once the message is terminal.
        b.Property(m => m.RecipientPhone).HasMaxLength(32).IsRequired();

        b.Property(m => m.IdempotencyKey).HasMaxLength(256).IsRequired();

        b.Property(m => m.Status)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SmsOutboxStatus>(v))
            .HasMaxLength(20);

        b.Property(m => m.LastError).HasMaxLength(2000);

        // A Twilio SID is 34 characters; Brevo's reference is shorter. 64 covers both and the
        // next provider.
        b.Property(m => m.ProviderMessageId).HasMaxLength(64);
        b.Property(m => m.ProviderStatus).HasMaxLength(32);

        b.HasIndex(m => m.IdempotencyKey).IsUnique();

        // What the processor polls on, mirroring the email outbox.
        b.HasIndex(m => new { m.Status, m.CreatedAt });
        b.HasIndex(m => new { m.TenantId, m.Status });

        // What a delivery callback looks a message up by. Filtered, because it is null on every
        // row until the provider answers and a full index would be mostly empty — and NOT unique:
        // a provider that reuses an id across accounts would otherwise make its own callback fail
        // to insert.
        b.HasIndex(m => m.ProviderMessageId)
            .HasFilter("provider_message_id IS NOT NULL")
            .HasDatabaseName("ix_sms_outbox_messages_provider_id");
    }
}

internal sealed class SmsTemplateConfiguration : IEntityTypeConfiguration<SmsTemplate>
{
    public void Configure(EntityTypeBuilder<SmsTemplate> b)
    {
        b.ToTable("sms_templates");
        b.HasKey(t => t.Id);

        b.Property(t => t.TemplateKey).HasMaxLength(100).IsRequired();
        b.Property(t => t.Locale).HasMaxLength(10).IsRequired();

        // Generous: a template is Scriban source, so a 160-character message can come from a body
        // several times that length once its placeholders and conditionals are counted.
        b.Property(t => t.Body).HasMaxLength(2000).IsRequired();

        // The renderer's lookup: a tenant's own row first, then the platform default. TenantId
        // leads and nulls are indexed too — they are the platform rows, which every tenant reads.
        b.HasIndex(t => new { t.TenantId, t.TemplateKey, t.Locale, t.IsActive })
            .HasDatabaseName("ix_sms_templates_resolution");
    }
}

internal sealed class SmsDeliveryLogConfiguration : IEntityTypeConfiguration<SmsDeliveryLog>
{
    public void Configure(EntityTypeBuilder<SmsDeliveryLog> b)
    {
        b.ToTable("sms_delivery_logs");
        b.HasKey(l => l.Id);

        b.Property(l => l.EventType)
            .HasConversion(v => v.ToString(), v => Enum.Parse<SmsDeliveryEventType>(v))
            .HasMaxLength(20);

        b.Property(l => l.MaskedPhone).HasMaxLength(32).IsRequired();
        b.Property(l => l.ProviderStatus).HasMaxLength(32);

        b.HasIndex(l => new { l.TenantId, l.RecordedAt });
        b.HasIndex(l => l.OutboxMessageId);
    }
}
