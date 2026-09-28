using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientSegmentHistoryConfiguration : IEntityTypeConfiguration<ClientSegmentHistory>
{
    public void Configure(EntityTypeBuilder<ClientSegmentHistory> builder)
    {
        builder.ToTable("client_segment_history");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();
        builder.Property(h => h.SegmentCode).HasMaxLength(30).IsRequired();
        builder.Property(h => h.RuleCode).HasMaxLength(60);

        builder.HasIndex(h => new { h.TenantId, h.ClientId });
        builder.HasIndex(h => new { h.TenantId, h.ClientId, h.ValidFrom });
    }
}
