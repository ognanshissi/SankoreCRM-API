namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

public sealed class TaskDeclineConfiguration : IEntityTypeConfiguration<TaskDecline>
{
    public void Configure(EntityTypeBuilder<TaskDecline> builder)
    {
        builder.ToTable("task_declines");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Reason).HasMaxLength(500).IsRequired();

        builder.HasIndex(d => new { d.TenantId, d.TaskId, d.DeclinedAt });
        builder.HasIndex(d => new { d.TenantId, d.AgentId });

        builder.HasOne<CrmTask>()
            .WithMany()
            .HasForeignKey(d => d.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
