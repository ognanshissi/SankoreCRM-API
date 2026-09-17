using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowRuleConfiguration : IEntityTypeConfiguration<WorkflowRule>
{
    public void Configure(EntityTypeBuilder<WorkflowRule> b)
    {
        b.ToTable("workflow_rules");
        b.HasKey(r => r.Id);

        b.Property(r => r.Field).HasMaxLength(200).IsRequired();
        b.Property(r => r.Value).HasMaxLength(1000).IsRequired();
        b.Property(r => r.RuleType).HasConversion<string>().HasMaxLength(50);
        b.Property(r => r.Operator).HasConversion<string>().HasMaxLength(50);

        b.HasIndex(r => new { r.StepId, r.RuleType });
    }
}
