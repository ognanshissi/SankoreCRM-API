using Microsoft.EntityFrameworkCore;

namespace Sankore.Api.Infrastructure.Audit;

/// <summary>
/// Dedicated DbContext for the append-only audit trail.
/// Kept entirely separate from every module's DbContext so that:
///  - the audit schema never participates in module transactions,
///  - the <c>audit</c> Postgres schema can have its own REVOKE grants, and
///  - no module ever takes a compile-time dependency on audit infrastructure.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options)
    : DbContext(options)
{
    public DbSet<AuditLogEntry> Entries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
    }
}
