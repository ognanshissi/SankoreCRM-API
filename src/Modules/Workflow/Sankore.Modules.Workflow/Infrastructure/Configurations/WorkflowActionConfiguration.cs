using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowActionConfiguration : IEntityTypeConfiguration<WorkflowAction>
{
    public void Configure(EntityTypeBuilder<WorkflowAction> b)
    {
        b.ToTable("workflow_actions");
        b.HasKey(a => a.Id);

        b.Property(a => a.ActionType).HasConversion<string>().HasMaxLength(50).IsRequired();
        b.Property(a => a.ConfigJson).HasColumnType("text").HasDefaultValue("{}").ValueGeneratedNever();
        b.Property(a => a.ExecutionOrder).HasDefaultValue(0).ValueGeneratedNever();

        b.HasIndex(a => new { a.TransitionId, a.ExecutionOrder });
    }
}
