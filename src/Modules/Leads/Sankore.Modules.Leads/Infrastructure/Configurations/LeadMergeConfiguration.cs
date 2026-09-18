namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class LeadMergeConfiguration : IEntityTypeConfiguration<LeadMerge>
{
    public void Configure(EntityTypeBuilder<LeadMerge> builder)
    {
        builder.ToTable("lead_merges");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.OverriddenFields).HasMaxLength(500);

        // Allow fast lookups by either participant in the merge.
        builder.HasIndex(m => new { m.TenantId, m.TargetLeadId });
        builder.HasIndex(m => new { m.TenantId, m.SourceLeadId });
    }
}
