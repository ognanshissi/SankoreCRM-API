namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

public sealed class TaskReassignmentConfiguration : IEntityTypeConfiguration<TaskReassignment>
{
    public void Configure(EntityTypeBuilder<TaskReassignment> builder)
    {
        builder.ToTable("task_reassignments");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Reason).HasMaxLength(500).IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.TaskId, r.ReassignedAt });
        builder.HasIndex(r => new { r.TenantId, r.NewAgentId });

        builder.HasOne<CrmTask>()
            .WithMany()
            .HasForeignKey(r => r.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
