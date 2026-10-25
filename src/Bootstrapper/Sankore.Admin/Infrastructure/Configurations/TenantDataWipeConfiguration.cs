using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

internal sealed class TenantDataWipeConfiguration : IEntityTypeConfiguration<TenantDataWipe>
{
    public void Configure(EntityTypeBuilder<TenantDataWipe> builder)
    {
        builder.ToTable("tenant_data_wipes");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(w => w.Summary).HasColumnType("text");
        builder.Property(w => w.Error).HasMaxLength(2000);
        builder.HasIndex(w => new { w.Status, w.RequestedAt });
        builder.HasIndex(w => new { w.TenantId, w.RequestedAt });

        // One wipe in flight per institution: a second click must not queue a second one.
        builder.HasIndex(w => w.TenantId)
            .IsUnique()
            .HasFilter("\"Status\" IN ('Pending', 'Running')")
            .HasDatabaseName("ux_tenant_data_wipes_in_flight");
    }
}
