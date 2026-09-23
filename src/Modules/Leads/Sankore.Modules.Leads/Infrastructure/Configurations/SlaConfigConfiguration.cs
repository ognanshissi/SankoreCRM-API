using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class SlaConfigConfiguration : IEntityTypeConfiguration<SlaConfig>
{
    public void Configure(EntityTypeBuilder<SlaConfig> builder)
    {
        builder.ToTable("sla_configs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(100);
        builder.HasIndex(e => new { e.TenantId, e.AgencyId, e.IsActive });
    }
}
