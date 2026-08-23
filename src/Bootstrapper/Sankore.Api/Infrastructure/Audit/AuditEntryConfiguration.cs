using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Sankore.Api.Infrastructure.Audit;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Action).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Outcome).HasMaxLength(20).IsRequired();
        builder.Property(e => e.PayloadJson).IsRequired();
        builder.Property(e => e.ErrorDetail).HasMaxLength(2000);
        builder.Property(e => e.ResourceType).HasMaxLength(100);
        builder.Property(e => e.ResourceId).HasMaxLength(100);
        builder.Property(e => e.IpAddress).HasMaxLength(45);   // covers IPv6
        builder.Property(e => e.UserAgent).HasMaxLength(500);
        builder.Property(e => e.CorrelationId).HasMaxLength(100);

        // Common read patterns — always scoped to TenantId first.
        builder.HasIndex(e => new { e.TenantId, e.Timestamp });
        builder.HasIndex(e => new { e.TenantId, e.UserId });
        builder.HasIndex(e => new { e.TenantId, e.Action });
        builder.HasIndex(e => new { e.TenantId, e.ResourceType, e.ResourceId });

        // ── Immutability note ─────────────────────────────────────────────
        // After running this migration, execute the following as a DBA:
        //
        //   REVOKE UPDATE, DELETE ON audit.entries FROM sankore_app;
        //
        // This revokes the ability to mutate rows at the DB level,
        // complementing the application-level init-only setters on AuditLogEntry.
    }
}
