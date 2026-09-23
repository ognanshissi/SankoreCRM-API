using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class ScoringConfigConfiguration: IEntityTypeConfiguration<ScoringConfig>
{
    public void Configure(EntityTypeBuilder<ScoringConfig> b)
    {
        b.ToTable("scoring_configs");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(100);
        b.HasIndex(e => new { e.TenantId, e.IsActive });
    }
}