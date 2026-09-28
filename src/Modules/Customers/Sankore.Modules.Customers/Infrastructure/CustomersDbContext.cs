namespace Sankore.Modules.Customers.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

/// <summary>
/// Persistence root of module M01. Lives in its own PostgreSQL schema
/// (<c>customers</c>) so the module boundary is enforced by the database and not
/// only by project references: nothing in this context ever joins a table owned by
/// another module, and no foreign key crosses the schema.
/// </summary>
public sealed class CustomersDbContext(DbContextOptions<CustomersDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    // ── Client aggregate ────────────────────────────────────────────────────
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientContactPoint> ClientContactPoints => Set<ClientContactPoint>();
    public DbSet<ClientStatusHistory> ClientStatusHistories => Set<ClientStatusHistory>();
    public DbSet<ClientNumberSequence> ClientNumberSequences => Set<ClientNumberSequence>();

    // ── Legal entities & relationships ──────────────────────────────────────
    public DbSet<BeneficialOwner> BeneficialOwners => Set<BeneficialOwner>();
    public DbSet<ClientRelationship> ClientRelationships => Set<ClientRelationship>();

    // ── Groups ──────────────────────────────────────────────────────────────
    public DbSet<ClientGroup> ClientGroups => Set<ClientGroup>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();

    // ── Timeline, segmentation & loyalty ────────────────────────────────────
    public DbSet<ClientTimelineEntry> ClientTimelineEntries => Set<ClientTimelineEntry>();
    public DbSet<ClientSegmentHistory> ClientSegmentHistories => Set<ClientSegmentHistory>();
    public DbSet<ClientLoyaltyScore> ClientLoyaltyScores => Set<ClientLoyaltyScore>();

    // ── Deduplication & merge ───────────────────────────────────────────────
    public DbSet<DuplicateCandidate> DuplicateCandidates => Set<DuplicateCandidate>();
    public DbSet<ClientMergeRequest> ClientMergeRequests => Set<ClientMergeRequest>();

    // ── Compliance ──────────────────────────────────────────────────────────
    public DbSet<SensitiveDataAccessLog> SensitiveDataAccessLogs => Set<SensitiveDataAccessLog>();
    public DbSet<ClientExportJob> ClientExportJobs => Set<ClientExportJob>();

    // ── Tenant configuration ────────────────────────────────────────────────
    public DbSet<CustomerSetting> CustomerSettings => Set<CustomerSetting>();
    public DbSet<LegalForm> LegalForms => Set<LegalForm>();

    // ── Messaging plumbing ──────────────────────────────────────────────────
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        // Reads dominate this module; handlers opt back in with .AsTracking()
        // whenever they intend to mutate an aggregate.
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("customers");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CustomersDbContext).Assembly);

        // Every aggregate and child entity in this module assigns its own Guid key in a
        // factory (Id = Guid.NewGuid()). EF's convention still marks such a key
        // ValueGenerated.OnAdd, and that is not cosmetic: when a child row is appended to
        // an already-tracked aggregate — a status-history line, a contact point, a group
        // membership — the change tracker sees a non-default key on a new instance and
        // classifies it as Modified instead of Added. SaveChangesAsync then throws
        // "Attempted to update or delete an entity that does not exist in the store".
        // Declaring it once here, over the whole model, is the only way a future entity
        // cannot forget it. The xmin concurrency tokens are untouched: they are not keys.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var key = entityType.FindPrimaryKey();
            if (key is null || key.Properties.Count != 1) continue;

            var keyProperty = key.Properties[0];
            if (keyProperty.ClrType == typeof(Guid))
                keyProperty.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        // Outbox lives in this module's own schema (customers.outbox_messages),
        // never a shared table — same rule as every other module.
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });

        // ── Multi-tenant isolation ──────────────────────────────────────────
        // Query filters stay centralised here (repo convention) because they
        // capture the `tenant` constructor parameter, which an
        // IEntityTypeConfiguration cannot see. Every tenant-scoped entity must
        // appear below: a missing line is a cross-tenant leak waiting to happen.
        modelBuilder.Entity<Client>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientContactPoint>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientStatusHistory>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientNumberSequence>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<BeneficialOwner>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientRelationship>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientGroup>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<GroupMembership>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientTimelineEntry>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientSegmentHistory>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientLoyaltyScore>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<DuplicateCandidate>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientMergeRequest>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<SensitiveDataAccessLog>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ClientExportJob>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CustomerSetting>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<LegalForm>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        // The inbox is tenant-scoped too: a replayed event from tenant A must never
        // be considered "already processed" because tenant B saw the same id.
        modelBuilder.Entity<InboxMessage>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
    }
}
