using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadSettingConfiguration : IEntityTypeConfiguration<LeadSetting>
{
    public void Configure(EntityTypeBuilder<LeadSetting> builder)
    {
        builder.ToTable("lead_settings");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Key).HasMaxLength(80).IsRequired();
        // Values are stored as text whatever their declared type: the settings service parses them
        // with InvariantCulture, so "25.5" means the same thing on every host locale.
        builder.Property(s => s.Value).HasMaxLength(4000).IsRequired();
        builder.Property(s => s.ValueType).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);

        builder.HasIndex(s => new { s.TenantId, s.Key })
            .IsUnique()
            .HasDatabaseName("ux_lead_settings_key");
    }
}
