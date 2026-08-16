using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

public class LeadAssignmentConfiguration: IEntityTypeConfiguration<LeadAssignment>
{
    public void Configure(EntityTypeBuilder<LeadAssignment> builder)
    {
        builder.ToTable("lead_assignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Strategy).HasConversion<string>().HasMaxLength(30);
        builder.HasIndex(a => a.LeadId);
        builder.HasIndex(a => a.AgentId); // no foreign key, the agency live inside UserModule
        builder.HasIndex(a => new { a.SlaDeadline, a.FirstContactAt });

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}