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
        b.Property(t => t.Version).HasDefaultValue(1).ValueGeneratedNever();

        // Each (tenant, entityType, version) triplet must be unique.
        b.HasIndex(t => new { t.TenantId, t.EntityType, t.Version }).IsUnique();

        // At most one active template per (tenant, entityType) — filtered unique index.
        b.HasIndex(t => new { t.TenantId, t.EntityType })
         .IsUnique()
         .HasFilter("is_active = true");

        b.HasMany(t => t.Steps)
         .WithOne()
         .HasForeignKey(s => s.TemplateId)
         .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(t => t.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(t => t.Transitions)
         .WithOne()
         .HasForeignKey(tr => tr.TemplateId)
         .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(t => t.Transitions).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.Ignore(t => t.DomainEvents);
    }
}
