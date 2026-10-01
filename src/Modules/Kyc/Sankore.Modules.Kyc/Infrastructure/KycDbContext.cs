namespace Sankore.Modules.Kyc.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

/// <summary>
/// Persistence root of module M02. Lives in its own PostgreSQL schema (<c>kyc</c>) so the module
/// boundary is enforced by the database and not only by project references: nothing here ever
/// joins a table owned by another module, and no foreign key crosses the schema. The customer is
/// referenced by an opaque <c>CustomerId</c>, never by an FK into <c>customers</c>.
/// </summary>
public sealed class KycDbContext(DbContextOptions<KycDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<KycFile> KycFiles => Set<KycFile>();

    // ── Verification evidence ───────────────────────────────────────────────
    public DbSet<KycIdentityDocument> KycIdentityDocuments => Set<KycIdentityDocument>();
    public DbSet<KycFaceVerification> KycFaceVerifications => Set<KycFaceVerification>();
    public DbSet<KycConfidenceAssessment> KycConfidenceAssessments => Set<KycConfidenceAssessment>();
    public DbSet<KycFieldCorrection> KycFieldCorrections => Set<KycFieldCorrection>();

    // ── Approval circuit and periodic reviews ───────────────────────────────
    public DbSet<KycApprovalStep> KycApprovalSteps => Set<KycApprovalStep>();
    public DbSet<KycReviewSchedule> KycReviewSchedules => Set<KycReviewSchedule>();

    /// <summary>Append-only: who opened which KYC image, and when.</summary>
    public DbSet<KycDocumentAccessLog> KycDocumentAccessLogs => Set<KycDocumentAccessLog>();
    public DbSet<KycSetting> KycSettings => Set<KycSetting>();

    /// <summary>Outbox lives in this module's own schema — same rule as every other module.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        // Reads dominate; handlers opt back in with .AsTracking() when they mean to mutate.
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("kyc");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KycDbContext).Assembly);

        // Every entity here assigns its own Guid key in a factory. EF's convention still marks
        // such a key ValueGenerated.OnAdd, and that is not cosmetic: a child row appended to an
        // already-tracked aggregate is then classified Modified instead of Added, and
        // SaveChangesAsync throws "Attempted to update or delete an entity that does not exist in
        // the store". M01 was bitten on 17 of its 18 configurations. Declaring it once over the
        // whole model is the only form a future entity cannot forget. xmin tokens are untouched:
        // they are not keys.
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
        modelBuilder.Entity<KycFile>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycSetting>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycIdentityDocument>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycFaceVerification>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycConfidenceAssessment>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycFieldCorrection>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycApprovalStep>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycReviewSchedule>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<KycDocumentAccessLog>().HasQueryFilter(x => x.TenantId == tenant.CurrentTenantId);
    }
}
