using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Shared.Kernel;

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
    public DbSet<ProductSpeciality> ProductSpecialities => Set<ProductSpeciality>();
    public DbSet<Territory> Territories => Set<Territory>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<TenantNotificationSettings> TenantNotificationSettings => Set<TenantNotificationSettings>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<CompanyInfo> CompanyInfos => Set<CompanyInfo>();
    public DbSet<UserImportJob> UserImportJobs => Set<UserImportJob>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        optionsBuilder.EnableSensitiveDataLogging();
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
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
        modelBuilder.Entity<AppUser>()
            .HasQueryFilter(u => u.TenantId == tenant.CurrentTenantId);
        
        modelBuilder.Entity<Agency>().HasQueryFilter(a => a.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<PasswordHistory>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<UserLoginLocation>().HasQueryFilter(l => l.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<UserRole>().HasQueryFilter(r => r.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<UserProfile>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<PermissionAttribution>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<Territory>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<ProductSpeciality>().HasQueryFilter(p => p.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<TenantNotificationSettings>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<RefreshToken>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);
        modelBuilder.Entity<CompanyInfo>().HasQueryFilter(s => s.TenantId == tenant.CurrentTenantId);

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
            b.HasQueryFilter(j => j.TenantId == tenant.CurrentTenantId);
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
