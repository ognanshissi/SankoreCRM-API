using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class DispatchingRuleConfiguration : IEntityTypeConfiguration<DispatchingRule>
{
    public void Configure(EntityTypeBuilder<DispatchingRule> builder)
    {
        builder.ToTable("dispatching_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Strategy).HasConversion(
            v => v.ToString(),
            v => Enum.Parse<DispatchingStrategy>(v))
            .HasMaxLength(30);
        builder.OwnsOne(r => r.Weights, w =>
        {
            w.Property(x => x.Language).HasColumnName("weight_language");
            w.Property(x => x.Product).HasColumnName("weight_product");
            w.Property(x => x.Geography).HasColumnName("weight_geography");
            w.Property(x => x.Workload).HasColumnName("weight_workload");
            w.Property(x => x.Performance).HasColumnName("weight_performance");
            w.Property(x => x.Agency).HasColumnName("weight_agency");
        });
        builder.Property(r => r.ExcludedAgentIds)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => (IReadOnlyList<Guid>)(JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new List<Guid>()));
        builder.HasIndex(r => new { r.TenantId, r.Strategy, r.IsActive, r.Priority });
    }
}
