using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowInstanceConfiguration : IEntityTypeConfiguration<WorkflowInstance>
{
    public void Configure(EntityTypeBuilder<WorkflowInstance> b)
    {
        b.ToTable("workflow_instances");
        b.HasKey(i => i.Id);

        b.Property(i => i.EntityType).HasMaxLength(100).IsRequired();
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(50);

        b.HasIndex(i => new { i.TenantId, i.EntityType, i.EntityId });

        b.HasMany(i => i.Steps)
         .WithOne()
         .HasForeignKey(s => s.InstanceId)
         .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(i => i.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
