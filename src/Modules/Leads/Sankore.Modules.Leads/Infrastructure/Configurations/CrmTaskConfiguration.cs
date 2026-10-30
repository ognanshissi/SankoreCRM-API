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

        builder.Property(t => t.Type).HasMaxLength(CrmTaskType.CodeMaxLength);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Title).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Description).HasMaxLength(CrmTask.DescriptionMaxLength);
        builder.Property(t => t.EntityType).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.TriggerEventType).HasMaxLength(100);
        builder.Property(t => t.CompatibilityFactorsJson)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("NULL");
        builder.Property(t => t.CompatibilityScore);

        builder.HasIndex(t => new { t.TenantId, t.Status, t.DueAt });
        builder.HasIndex(t => new { t.LeadId, t.Status });
        builder.HasIndex(t => new { t.AssignedAgentId, t.Status });
        // The tasks about a client or a KYC file — the record's own tab.
        builder.HasIndex(t => new { t.TenantId, t.EntityType, t.EntityId });

        // Optional FK to lead — leads live in the same module/schema
        builder.HasOne<Lead>()
            .WithMany()
            .HasForeignKey(t => t.LeadId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class CrmTaskParticipantConfiguration : IEntityTypeConfiguration<CrmTaskParticipant>
{
    public void Configure(EntityTypeBuilder<CrmTaskParticipant> b)
    {
        b.ToTable("crm_task_participants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => new { x.TaskId, x.UserId }).IsUnique();
        // « Les tâches où je suis associé ».
        b.HasIndex(x => new { x.TenantId, x.UserId });
        b.HasOne<CrmTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CrmTaskCriterionConfiguration : IEntityTypeConfiguration<CrmTaskCriterion>
{
    public void Configure(EntityTypeBuilder<CrmTaskCriterion> b)
    {
        b.ToTable("crm_task_criteria");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Label).HasMaxLength(CrmTaskCriterion.LabelMaxLength).IsRequired();
        b.HasIndex(x => new { x.TaskId, x.Position });
        b.HasOne<CrmTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CrmTaskCommentConfiguration : IEntityTypeConfiguration<CrmTaskComment>
{
    public void Configure(EntityTypeBuilder<CrmTaskComment> b)
    {
        b.ToTable("crm_task_comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Body).HasMaxLength(CrmTaskComment.BodyMaxLength).IsRequired();
        b.HasIndex(x => new { x.TaskId, x.CreatedAt });
        b.HasOne<CrmTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CrmTaskTagConfiguration : IEntityTypeConfiguration<CrmTaskTag>
{
    public void Configure(EntityTypeBuilder<CrmTaskTag> b)
    {
        b.ToTable("crm_task_tags");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Tag).HasMaxLength(CrmTaskTag.MaxLength).IsRequired();
        b.HasIndex(x => new { x.TaskId, x.Tag }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Tag });
        b.HasOne<CrmTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class CrmTaskAttachmentConfiguration : IEntityTypeConfiguration<CrmTaskAttachment>
{
    public void Configure(EntityTypeBuilder<CrmTaskAttachment> b)
    {
        b.ToTable("crm_task_attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FileName).HasMaxLength(CrmTaskAttachment.FileNameMaxLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.StorageRef).HasMaxLength(80).IsRequired();
        b.HasIndex(x => new { x.TaskId, x.UploadedAt });
        b.HasOne<CrmTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
    }
}
