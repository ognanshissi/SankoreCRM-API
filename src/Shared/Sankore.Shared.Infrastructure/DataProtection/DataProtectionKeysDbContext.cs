namespace Sankore.Shared.Infrastructure.DataProtection;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Storage for the ASP.NET Data Protection key ring.
///
/// Its own DbContext (and its own schema) rather than a table on a module context: the key ring
/// is host-wide shared infrastructure that no module owns, and it must be readable before any
/// module has been initialised — Identity token providers are built during
/// <c>AddDefaultTokenProviders()</c>, not on first use.
/// </summary>
public sealed class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dataprotection");

        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.ToTable("keys");
            b.HasKey(k => k.Id);
            b.Property(k => k.FriendlyName).HasMaxLength(200);
            b.Property(k => k.Xml).IsRequired();
        });
    }
}
