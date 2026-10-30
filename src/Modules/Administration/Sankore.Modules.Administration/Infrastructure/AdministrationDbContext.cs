using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Shared.Kernel;
using Sankore.Shared.Infrastructure.Persistence;

public sealed class AdministrationDbContext(DbContextOptions<AdministrationDbContext> options, ITenantContext tenant)
    : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Agency> Agencies => Set<Agency>();
    public DbSet<UserLoginLocation> UserLoginLocations => Set<UserLoginLocation>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<PasswordHistory> PasswordHistories => Set<PasswordHistory>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<PermissionAttribution> PermissionAttributions => Set<PermissionAttribution>();
    public DbSet<AgentSettings> AgentSettings => Set<AgentSettings>();
    public DbSet<UserAvatar> UserAvatars => Set<UserAvatar>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ProductSpeciality> ProductSpecialities => Set<ProductSpeciality>();
    public DbSet<Territory> Territories => Set<Territory>();
    /// <summary>
    /// The tenant-scoped audit mirror of role grants (table <c>user_roles</c>, own Id, soft
    /// revoke via <c>IsActive</c>) — NOT Identity's <c>IdentityUserRole&lt;Guid&gt;</c> join
    /// table, which this deliberately shadows and which login still reads through
    /// <c>UserManager.GetRolesAsync</c>. `new` is explicit so the shadowing reads as intended
    /// rather than as a missing `override`: every grant must be written to BOTH stores
    /// (see AssignRole, RevokeRole, BulkAssignRole, AgencyManagerRoleCoordinator).
    /// </summary>
    public new DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<TenantNotificationSettings> TenantNotificationSettings => Set<TenantNotificationSettings>();

    /// <summary>The tenant's SMS channel — its own row beside the email one; see the entity.</summary>
    public DbSet<TenantSmsSettings> TenantSmsSettings => Set<TenantSmsSettings>();
    public DbSet<ComplianceSettings> ComplianceSettings => Set<ComplianceSettings>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<CompanyInfo> CompanyInfos => Set<CompanyInfo>();
    public DbSet<CompanyLogo> CompanyLogos => Set<CompanyLogo>();
    public DbSet<UserImportJob> UserImportJobs => Set<UserImportJob>();

    /// <summary>Professional cards and their indemnity cover (CIMA articles 503, 510, 732).</summary>
    public DbSet<DistributionCredential> DistributionCredentials => Set<DistributionCredential>();
    public DbSet<InsuranceDistributorProfile> InsuranceDistributorProfiles => Set<InsuranceDistributorProfile>();
    public DbSet<DistributorDocument> DistributorDocuments => Set<DistributorDocument>();

    /// <summary>The tenant's layout of the lead, client and product forms (dynamic forms, lot 1).</summary>
    public DbSet<Domain.Forms.EntityFormLayout> EntityFormLayouts => Set<Domain.Forms.EntityFormLayout>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
    }

/// <summary>
/// Every timestamp column stores UTC — Npgsql rejects any other offset outright.
/// See <see cref="UtcTimestamps"/>.
/// </summary>
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
{
    base.ConfigureConventions(builder);
    builder.UseUtcDateTimeOffsets();
}

protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Dedicated PostgreSQL schema: enforces the module boundary at the
        // database level, not just in code.
        modelBuilder.HasDefaultSchema("administration");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdministrationDbContext).Assembly);
        // AppRole — add IsSystem on top of standard Identity columns (keep AspNetRoles table name)
        modelBuilder.Entity<AppRole>(b =>
        {
            b.ToTable("app_roles");
            b.Property(r => r.IsSystem).HasDefaultValue(true);
        });

        // Permissions
        modelBuilder.Entity<Permission>(b =>
        {
            b.ToTable("permissions");
            b.HasKey(r => r.Id);
        });

        // RolePermission — join between AppRole and Permission
        modelBuilder.Entity<RolePermission>(b =>
        {
            b.ToTable("role_permissions");
            b.HasKey(r => r.Id);
            b.HasOne(r => r.Role)
                .WithMany()
                .HasForeignKey(r => r.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(r => r.Permission)
                .WithMany()
                .HasForeignKey(r => r.PermissionId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(r => new { r.RoleId, r.PermissionId }).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });

        // Multi-tenant isolation enforced at the ORM level: no query can
        // ever leak rows across tenants, even if a handler forgets to filter.
        //
        // Sandbox lot 2: the TEAM — accounts, roles, agencies, territories, sessions and the
        // institution's own identity — is shared by both modes and filters on TeamTenantId, the
        // live tenant even in « Mode test ». CONFIGURATION — products, forms, notification and SMS
        // channels, compliance settings — is the mode's own and filters on CurrentTenantId.
        modelBuilder.Entity<AppUser>()
            .HasQueryFilter(u => u.TenantId == tenant.TeamTenantId);
        
        modelBuilder.Entity<Agency>().HasQueryFilter(a => a.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<PasswordHistory>().HasQueryFilter(p => p.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<UserLoginLocation>().HasQueryFilter(l => l.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<UserRole>().HasQueryFilter(r => r.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<UserProfile>().HasQueryFilter(p => p.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<PermissionAttribution>().HasQueryFilter(p => p.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<AgentSettings>().HasQueryFilter(s => s.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<UserAvatar>().HasQueryFilter(a => a.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<UserSession>().HasQueryFilter(s => s.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<Territory>().HasQueryFilter(p => p.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<ProductSpeciality>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<TenantNotificationSettings>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<TenantSmsSettings>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ComplianceSettings>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(s => s.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<CompanyInfo>().HasQueryFilter(s => s.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<CompanyLogo>().HasQueryFilter(l => l.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<DistributionCredential>().HasQueryFilter(c => c.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<InsuranceDistributorProfile>().HasQueryFilter(p => p.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<DistributorDocument>().HasQueryFilter(d => d.TenantId == tenant.TeamTenantId);
        modelBuilder.Entity<Domain.Forms.EntityFormLayout>().HasQueryFilter(f => f.TenantId == tenant.CurrentTenantId);

        modelBuilder.Entity<UserImportJob>(b =>
        {
            b.ToTable("user_import_jobs");
            b.HasKey(j => j.Id);
            b.Property(j => j.SourceType).HasConversion<string>().HasMaxLength(20);
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(j => j.SourceReference).HasMaxLength(500);
            b.Property(j => j.OriginalFileName).HasMaxLength(300);
            b.Property(j => j.ErrorMessage).HasMaxLength(2000);
            b.Property(j => j.FailureDetailsJson).HasColumnType("jsonb");
            b.HasQueryFilter(j => j.TenantId == tenant.TeamTenantId);
        });

        // Sequence creation
        modelBuilder.HasSequence<int>("GeneratedIncrementalNo", schema: "shared")
            .StartsAt(1000)
            .IncrementsBy(5);

        modelBuilder.HasSequence<long>("agency_code_seq", schema: "administration")
            .StartsAt(1)
            .IncrementsBy(1);
    }
}
