using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Shared.Infrastructure.Persistence;

namespace Sankore.Admin.Infrastructure;

public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options): DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<PlatformAdminRefreshToken> PlatformAdminRefreshTokens => Set<PlatformAdminRefreshToken>();
    public DbSet<ModuleDefinition> ModuleDefinitions => Set<ModuleDefinition>();
    public DbSet<TenantModuleSubscription> TenantModuleSubscriptions => Set<TenantModuleSubscription>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<TenantDataWipe> TenantDataWipes => Set<TenantDataWipe>();

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
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdminDbContext).Assembly);
    }
}