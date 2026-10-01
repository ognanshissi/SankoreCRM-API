namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycSettingConfiguration : IEntityTypeConfiguration<KycSetting>
{
    public void Configure(EntityTypeBuilder<KycSetting> b)
    {
        b.ToTable("kyc_settings");
        b.HasKey(s => s.Id);

        b.Property(s => s.Key).HasMaxLength(100).IsRequired();
        b.Property(s => s.Value).HasMaxLength(2000).IsRequired();
        b.Property(s => s.ValueType).HasMaxLength(16).IsRequired();
        b.Property(s => s.Description).HasMaxLength(500).IsRequired();

        b.HasIndex(s => new { s.TenantId, s.Key })
            .IsUnique()
            .HasDatabaseName("ux_kyc_settings_tenant_key");

        b.Ignore(s => s.DomainEvents);
    }
}
