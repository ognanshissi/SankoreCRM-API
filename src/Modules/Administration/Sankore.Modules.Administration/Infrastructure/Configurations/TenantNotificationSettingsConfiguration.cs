using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class TenantNotificationSettingsConfiguration
    : IEntityTypeConfiguration<TenantNotificationSettings>
{
    public void Configure(EntityTypeBuilder<TenantNotificationSettings> b)
    {
        b.ToTable("tenant_notification_settings");
        b.HasKey(s => s.Id);

        b.Property(s => s.ProviderType).HasMaxLength(20).IsRequired();
        b.Property(s => s.FromEmail).HasMaxLength(256);
        b.Property(s => s.FromName).HasMaxLength(200);
        b.Property(s => s.ReplyToEmail).HasMaxLength(256);
        b.Property(s => s.SendingDomain).HasMaxLength(253);
        b.Property(s => s.CredentialVaultPath).HasMaxLength(500);

        // One row per tenant — enforced by unique index
        b.HasIndex(s => s.TenantId).IsUnique();
    }
}
