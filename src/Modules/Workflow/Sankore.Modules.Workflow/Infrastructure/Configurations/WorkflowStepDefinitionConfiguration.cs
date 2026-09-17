using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowStepDefinitionConfiguration : IEntityTypeConfiguration<WorkflowStepDefinition>
{
    public void Configure(EntityTypeBuilder<WorkflowStepDefinition> b)
    {
        b.ToTable("workflow_step_definitions");
        b.HasKey(s => s.Id);

        b.Property(s => s.Code).HasMaxLength(100).IsRequired()
         .HasDefaultValue("STEP_0").ValueGeneratedNever();
        b.Property(s => s.StateType).HasConversion<string>().HasMaxLength(50)
         .HasDefaultValue(StateType.Approval).ValueGeneratedNever();
        b.Property(s => s.Name).HasMaxLength(200).IsRequired();
        b.Property(s => s.Description).HasMaxLength(1000);
        b.Property(s => s.ApproverRoleCode).HasMaxLength(100);

        b.HasIndex(s => new { s.TemplateId, s.Order }).IsUnique();

        b.HasMany(s => s.Rules)
         .WithOne()
         .HasForeignKey(r => r.StepId)
         .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(s => s.Rules).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
