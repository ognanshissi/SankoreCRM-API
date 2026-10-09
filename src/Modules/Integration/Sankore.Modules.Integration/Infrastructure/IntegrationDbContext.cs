namespace Sankore.Modules.Integration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

/// <summary>
/// The module's store: schema <c>integration</c>, migrations history in the same schema.
///
/// <para>
/// One context for both families of connector (core banking and insurance). That is the point of
/// ASS-01: the outbox, the dispatcher, the batch socle and the reconciliation are written once
/// and a second family plugs in without a second schema.
/// </para>
/// </summary>
public sealed class IntegrationDbContext(
    DbContextOptions<IntegrationDbContext> options,
    ITenantContext tenant) : DbContext(options)
{
    public DbSet<IntegrationConnection> Connections => Set<IntegrationConnection>();
    public DbSet<IntegrationCommand> Commands => Set<IntegrationCommand>();
    public DbSet<IntegrationReference> References => Set<IntegrationReference>();
    public DbSet<IntegrationBatchFile> BatchFiles => Set<IntegrationBatchFile>();
    public DbSet<IntegrationSyncCursor> SyncCursors => Set<IntegrationSyncCursor>();
    public DbSet<IntegrationMapping> Mappings => Set<IntegrationMapping>();
    public DbSet<IntegrationReconciliationRun> ReconciliationRuns => Set<IntegrationReconciliationRun>();
    public DbSet<IntegrationReconciliationGap> ReconciliationGaps => Set<IntegrationReconciliationGap>();
    public DbSet<IntegrationCallLog> CallLogs => Set<IntegrationCallLog>();
    public DbSet<CbsSnapshot> CbsSnapshots => Set<CbsSnapshot>();
    public DbSet<KycLimitAlert> KycLimitAlerts => Set<KycLimitAlert>();
    public DbSet<IntegrationRelayAgent> RelayAgents => Set<IntegrationRelayAgent>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<IntegrationInboxMessage> InboxMessages => Set<IntegrationInboxMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Reads dominate; handlers opt back in with .AsTracking() when they mean to mutate.
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("integration");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegrationDbContext).Assembly);

        // Repo-wide: a Guid primary key is assigned by the domain, never by the database. Without
        // this EF treats it as store-generated and skips the value the factory set.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var key = entityType.FindPrimaryKey();
            if (key is null || key.Properties.Count != 1) continue;

            var keyProperty = key.Properties[0];
            if (keyProperty.ClrType == typeof(Guid))
                keyProperty.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });

        // ── Multi-tenant isolation ──────────────────────────────────────────
        // Query filters stay centralised here (repo convention) because they capture the `tenant`
        // constructor parameter, which an IEntityTypeConfiguration cannot see. Every tenant-scoped
        // entity must appear below: a missing line is a cross-tenant leak waiting to happen.
        //
        // OutboxMessage and IntegrationInboxMessage are deliberately absent — they are
        // infrastructure rows read by a processor that runs outside any tenant context, and
        // OutboxProcessor additionally calls IgnoreQueryFilters().
        modelBuilder.Entity<IntegrationConnection>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationCommand>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationReference>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationBatchFile>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationSyncCursor>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationMapping>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationReconciliationRun>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationReconciliationGap>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationCallLog>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CbsSnapshot>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycLimitAlert>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<IntegrationRelayAgent>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
    }
}

/// <summary>
/// Idempotency ledger for this module's MassTransit consumers. Module-owned, like M01's and
/// M02's: delivery is at-least-once, and the primary key is what makes a redelivery a no-op.
///
/// <para>
/// The id is NOT the raw message id. <c>KycValidatedEvent</c> is already consumed by M01, and
/// this module consumes it too (INT-14); guarding on the bare message id would let whichever
/// module ran first starve the other. Each consumer claims its own deterministic derivative.
/// </para>
/// </summary>
public sealed class IntegrationInboxMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
}
