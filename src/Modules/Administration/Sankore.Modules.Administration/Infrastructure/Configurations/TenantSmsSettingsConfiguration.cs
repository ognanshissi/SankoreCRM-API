using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class TenantSmsSettingsConfiguration : IEntityTypeConfiguration<TenantSmsSettings>
{
    public void Configure(EntityTypeBuilder<TenantSmsSettings> b)
    {
        b.ToTable("tenant_sms_settings");
        b.HasKey(s => s.Id);

        b.Property(s => s.ProviderType).HasMaxLength(20).IsRequired();

        // Long enough for a Twilio messaging-service SID (34 chars) and for an E.164 number,
        // which is what the other provider puts here. Brevo's alphanumeric sender is 11.
        b.Property(s => s.SenderId).HasMaxLength(64);

        // A Twilio Account SID is exactly 34 characters; the cap is generous on purpose, because
        // a provider renaming its identifier format must not need a migration.
        b.Property(s => s.AccountSid).HasMaxLength(64);

        // Digits only; the longest calling code in use is three.
        b.Property(s => s.DefaultCallingCode).HasMaxLength(4);

        // Same concurrency token as the email row, and for the same reason: TryConsumeMonthlyQuota
        // is check-and-increment in one, and only xmin makes that hold across processes.
        b.Property(s => s.Version)
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // One row per tenant.
        b.HasIndex(s => s.TenantId).IsUnique();
    }
}
