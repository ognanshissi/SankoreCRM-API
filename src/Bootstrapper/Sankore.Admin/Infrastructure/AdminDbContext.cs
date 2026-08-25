using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure;

public sealed class AdminDbContext(DbContextOptions<AdminDbContext> options): DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdminDbContext).Assembly);
    }
}