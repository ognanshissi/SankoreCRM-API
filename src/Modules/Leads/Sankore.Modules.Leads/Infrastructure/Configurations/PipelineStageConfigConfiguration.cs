using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class PipelineStageConfigConfiguration : IEntityTypeConfiguration<PipelineStageConfig>
{
    public void Configure(EntityTypeBuilder<PipelineStageConfig> builder)
    {
        builder.ToTable("pipeline_stage_configs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Code).HasMaxLength(30);
        builder.Property(e => e.Label).HasMaxLength(100);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.Property(e => e.Color).HasMaxLength(7);
        builder.HasIndex(e => new { e.TenantId, e.Code }).IsUnique();
    }
}
