namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

public sealed class CrmTaskConfiguration : IEntityTypeConfiguration<CrmTask>
{
    public void Configure(EntityTypeBuilder<CrmTask> builder)
    {
        builder.ToTable("crm_tasks");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Title).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(1000);
        builder.Property(t => t.TriggerEventType).HasMaxLength(100);
        builder.Property(t => t.CompatibilityFactorsJson)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("NULL");
        builder.Property(t => t.CompatibilityScore);

        builder.HasIndex(t => new { t.TenantId, t.Status, t.DueAt });
        builder.HasIndex(t => new { t.LeadId, t.Status });
        builder.HasIndex(t => new { t.AssignedAgentId, t.Status });

        // Optional FK to lead — leads live in the same module/schema
        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(t => t.LeadId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
