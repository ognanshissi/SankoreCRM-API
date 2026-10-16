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
        // As a STRING, like every other enum of this solution: a contact rate is read off this
        // column for years, and an enum stored as an integer is a column whose meaning changes the
        // day somebody inserts a value in the middle of the declaration.
        builder.Property(a => a.FirstContactSource).HasConversion<string>().HasMaxLength(20);

        builder.Property(a => a.CompatibilityFactorsJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");

        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(a => a.LeadId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}