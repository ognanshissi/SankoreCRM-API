using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowTaskConfiguration : IEntityTypeConfiguration<WorkflowTask>
{
    public void Configure(EntityTypeBuilder<WorkflowTask> b)
    {
        b.ToTable("workflow_tasks");
        b.HasKey(t => t.Id);

        b.Property(t => t.Title).HasMaxLength(200).IsRequired();
        b.Property(t => t.Description).HasMaxLength(2000);
        b.Property(t => t.AssignedRoleCode).HasMaxLength(100);
        b.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(TaskPriority.Normal).ValueGeneratedNever();
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20)
            .HasDefaultValue(WorkflowTaskStatus.Pending).ValueGeneratedNever();
        b.Property(t => t.CompletionComment).HasMaxLength(2000);

        b.Ignore(t => t.DomainEvents);

        b.HasIndex(t => new { t.TenantId, t.InstanceId });
        b.HasIndex(t => new { t.TenantId, t.AssignedToUserId, t.Status });
    }
}
