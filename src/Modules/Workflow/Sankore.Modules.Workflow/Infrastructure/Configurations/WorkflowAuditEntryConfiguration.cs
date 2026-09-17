using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Configurations;

internal sealed class WorkflowAuditEntryConfiguration : IEntityTypeConfiguration<WorkflowAuditEntry>
{
    public void Configure(EntityTypeBuilder<WorkflowAuditEntry> b)
    {
        b.ToTable("workflow_audit_entries");
        b.HasKey(a => a.Id);

        b.Property(a => a.EventCode).HasMaxLength(50).IsRequired();
        b.Property(a => a.Comment).HasMaxLength(2000);
        b.Property(a => a.ContextSnapshot).HasColumnType("text");

        // Append-only: never update existing rows.
        b.ToTable(t => t.ExcludeFromMigrations(false));

        b.HasIndex(a => new { a.TenantId, a.InstanceId, a.OccurredAt });
    }
}
