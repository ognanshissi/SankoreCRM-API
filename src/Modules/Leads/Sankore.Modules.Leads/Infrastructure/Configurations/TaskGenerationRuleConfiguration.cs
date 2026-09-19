namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

public sealed class TaskGenerationRuleConfiguration : IEntityTypeConfiguration<TaskGenerationRule>
{
    public void Configure(EntityTypeBuilder<TaskGenerationRule> builder)
    {
        builder.ToTable("task_generation_rules");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TriggerEventType).HasMaxLength(100).IsRequired();
        builder.Property(r => r.TaskType).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.TitleTemplate).HasMaxLength(200).IsRequired();
        builder.Property(r => r.DescriptionTemplate).HasMaxLength(1000);

        builder.HasIndex(r => new { r.TenantId, r.TriggerEventType, r.IsActive });
    }
}
