using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadSourceRunsConfiguration: IEntityTypeConfiguration<LeadSourceRun>
{
    public void Configure(EntityTypeBuilder<LeadSourceRun> b)
    {
        b.ToTable("lead_source_runs");
        b.HasKey(r => r.Id);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.ErrorMessage).HasMaxLength(2000);
        b.HasIndex(r => new { r.TenantId, r.SourceId, r.StartedAt });
    }
}