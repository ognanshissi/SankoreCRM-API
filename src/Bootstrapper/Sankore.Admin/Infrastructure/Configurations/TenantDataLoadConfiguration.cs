using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

internal sealed class TenantDataLoadConfiguration : IEntityTypeConfiguration<TenantDataLoad>
{
    public void Configure(EntityTypeBuilder<TenantDataLoad> builder)
    {
        builder.ToTable("tenant_data_loads");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(l => l.Summary).HasColumnType("text");
        builder.Property(l => l.Error).HasMaxLength(2000);
        builder.HasIndex(l => new { l.Status, l.RequestedAt });
        builder.HasIndex(l => new { l.TenantId, l.RequestedAt });

        // One load in flight per institution: two loads at once would race on the same natural keys.
        builder.HasIndex(l => l.TenantId)
            .IsUnique()
            .HasFilter("\"Status\" IN ('Pending', 'Running')")
            .HasDatabaseName("ux_tenant_data_loads_in_flight");
    }
}
