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

        // ── Immutability ──────────────────────────────────────────────────
        // ENFORCED, and not by this file: the `ImmutableEntries` migration installs three
        // PostgreSQL triggers over audit.entries that raise on UPDATE, DELETE and TRUNCATE,
        // whatever the application does and whatever role is connected. The init-only setters on
        // AuditLogEntry are the second layer, not the guarantee.
        //
        // This comment used to read as an instruction — "execute the following as a DBA:
        // REVOKE UPDATE, DELETE ON audit.entries FROM sankore_app" — written before the
        // triggers existed. It outlived them, and anybody auditing the repository read it as
        // meaning the only protection was a manual step nobody had taken. A REVOKE is still a
        // reasonable defence in depth, so it lives in docs/deployment-dokploy.md as an optional
        // hardening step rather than here as a prerequisite.
    }
}
