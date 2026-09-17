using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowTriggerConfiguration : IEntityTypeConfiguration<WorkflowTrigger>
{
    public void Configure(EntityTypeBuilder<WorkflowTrigger> b)
    {
        b.ToTable("workflow_triggers");
        b.HasKey(t => t.Id);

        b.Property(t => t.EventName).HasMaxLength(100).IsRequired();
        b.Property(t => t.TriggerType).HasConversion<string>().HasMaxLength(50);
        b.Property(t => t.ConditionJson).HasColumnType("text");
        b.Property(t => t.IsActive).HasDefaultValue(true).ValueGeneratedNever();

        // Each template may only have one active trigger per event name.
        b.HasIndex(t => new { t.TenantId, t.TemplateId, t.EventName }).IsUnique();

        // Fast lookup by consumer: find all matching triggers for an incoming signal.
        b.HasIndex(t => new { t.TenantId, t.EventName, t.IsActive });
    }
}
