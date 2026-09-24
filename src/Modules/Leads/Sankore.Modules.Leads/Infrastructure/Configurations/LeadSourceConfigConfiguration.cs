using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadSourceConfigConfiguration: IEntityTypeConfiguration<LeadSourceConfig>
{
    public void Configure(EntityTypeBuilder<LeadSourceConfig> b)
    {
         
            b.ToTable("lead_source_configs");
            b.HasKey(e => e.Id);
            b.Property(e => e.Code).HasMaxLength(30).IsRequired();
            b.Property(e => e.Label).HasMaxLength(100).IsRequired();
            b.Property(e => e.Description).HasMaxLength(500);
            b.Property(e => e.ChannelType).HasConversion<string>().HasMaxLength(30).IsRequired();
            b.Property(e => e.Mode).HasConversion<string>().HasMaxLength(30).IsRequired();
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.PublicKey).HasMaxLength(128);
            b.Property(e => e.Settings)
                .HasColumnType("jsonb")
                .HasConversion(new Configurations.SourceSettingsConverter())
                .Metadata.SetValueComparer(new Configurations.SourceSettingsComparer());
            b.Property(e => e.PlatformConnectionId).HasMaxLength(100);
            b.Property(e => e.LastPingOrigin).HasMaxLength(500);
            b.Property(e => e.UnauthorizedOriginSeen).HasMaxLength(500);
            b.Property(e => e.LastPullCursor).HasMaxLength(2000);
            b.Property(e => e.LastError).HasMaxLength(2000);
            b.Property(e => e.Version).HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            b.OwnsOne(e => e.CostPerLead, m =>
            {
                m.Property(p => p.Amount).HasColumnName("cost_per_lead").HasPrecision(18, 4);
                m.Property(p => p.Currency).HasColumnName("cost_currency").HasMaxLength(3);
            });

            // ux_lead_sources_code — unique code per tenant
            b.HasIndex(e => new { e.TenantId, e.Code })
                .IsUnique()
                .HasDatabaseName("ux_lead_sources_code");

            // ux_lead_sources_public_key — unique non-null public keys across all tenants
            b.HasIndex(e => e.PublicKey)
                .IsUnique()
                .HasFilter("public_key IS NOT NULL")
                .HasDatabaseName("ux_lead_sources_public_key");

            // ix_lead_sources_pull — for Pull source scheduler queries
            b.HasIndex(e => new { e.TenantId, e.Mode, e.Status })
                .HasFilter("mode = 'ScheduledPull' AND status = 'Active'")
                .HasDatabaseName("ix_lead_sources_pull");

    }
}