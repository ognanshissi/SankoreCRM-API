using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadImportJobsConfiguration: IEntityTypeConfiguration<LeadImportJob>
{
    public void Configure(EntityTypeBuilder<LeadImportJob> b)
    {
        b.ToTable("lead_import_jobs");
        b.HasKey(j => j.Id);
        b.Property(j => j.Status).HasConversion(
                v => v.ToString(),
                v => Enum.Parse<LeadImportStatus>(v))
            .HasMaxLength(20);
    }
}