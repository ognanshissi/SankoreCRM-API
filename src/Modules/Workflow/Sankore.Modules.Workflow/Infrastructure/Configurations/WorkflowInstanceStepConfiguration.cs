using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowInstanceStepConfiguration : IEntityTypeConfiguration<WorkflowInstanceStep>
{
    public void Configure(EntityTypeBuilder<WorkflowInstanceStep> b)
    {
        b.ToTable("workflow_instance_steps");
        b.HasKey(s => s.Id);

        b.Property(s => s.Name).HasMaxLength(200).IsRequired();
        b.Property(s => s.ApproverRoleCode).HasMaxLength(100);
        b.Property(s => s.Comment).HasMaxLength(2000);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(50);

        b.HasIndex(s => new { s.InstanceId, s.Order });
    }
}
