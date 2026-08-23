using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowTemplateConfiguration : IEntityTypeConfiguration<WorkflowTemplate>
{
    public void Configure(EntityTypeBuilder<WorkflowTemplate> b)
    {
        b.ToTable("workflow_templates");
        b.HasKey(t => t.Id);

        b.Property(t => t.EntityType).HasMaxLength(100).IsRequired();
        b.Property(t => t.Name).HasMaxLength(200).IsRequired();
        b.Property(t => t.Description).HasMaxLength(1000);

        // At most one active template per (tenant, entityType)
        b.HasIndex(t => new { t.TenantId, t.EntityType })
         .IsUnique()
         .HasFilter("\"IsActive\" = true");

        b.HasMany(t => t.Steps)
         .WithOne()
         .HasForeignKey(s => s.TemplateId)
         .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(t => t.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
