using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientStatusHistoryConfiguration : IEntityTypeConfiguration<ClientStatusHistory>
{
    public void Configure(EntityTypeBuilder<ClientStatusHistory> builder)
    {
        builder.ToTable("client_status_history");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();
        builder.Property(h => h.OldStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(h => h.NewStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(h => h.Reason).HasMaxLength(1000);

        builder.HasIndex(h => new { h.TenantId, h.ClientId });
        builder.HasIndex(h => new { h.TenantId, h.ClientId, h.OccurredAt });
    }
}
