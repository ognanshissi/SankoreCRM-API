namespace Sankore.Modules.Users.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Users.Domain;
using Sankore.Shared.Infrastructure.Outbox;
using Sankore.Shared.Kernel;

public sealed class UsersDbContext(DbContextOptions<UsersDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Dedicated PostgreSQL schema: enforces the module boundary at the
        // database level, not just in code.
        modelBuilder.HasDefaultSchema("users");

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("app_users");
            b.HasKey(u => u.Id);
            b.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            b.Property(u => u.Email).HasMaxLength(200).IsRequired();

            b.OwnsOne<GeoPoint>(u => u.LastKnownLocation, loc =>
            {
                loc.Property(l => l.Latitude).HasColumnName("last_lat");
                loc.Property(l => l.Longitude).HasColumnName("last_lng");
            });

            b.Property(u => u.SpokenLanguages)
                .HasColumnType("text[]");
            b.Property(u => u.Specialties)
                .HasColumnType("text[]");

            b.HasIndex(u => new { u.TenantId, u.AgencyId });
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
    }
}
