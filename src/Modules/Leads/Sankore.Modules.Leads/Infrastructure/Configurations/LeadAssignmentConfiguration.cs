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
        // Filtered on open rows: CheckSlaBreachesJob and GetSlaBreaches now both exclude superseded
        // assignments, and those accumulate — one per reassignment, for ever.
        builder.HasIndex(a => new { a.SlaDeadline, a.FirstContactAt })
            .HasFilter("superseded_at IS NULL");
        builder.Property(a => a.CompatibilityFactorsJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}