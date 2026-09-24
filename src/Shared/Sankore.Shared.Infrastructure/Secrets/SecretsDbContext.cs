namespace Sankore.Shared.Infrastructure.Secrets;

using Microsoft.EntityFrameworkCore;

public sealed class SecretsDbContext(DbContextOptions<SecretsDbContext> options)
    : DbContext(options)
{
    public DbSet<SecretEntry> Entries => Set<SecretEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("secrets");

        modelBuilder.Entity<SecretEntry>(b =>
        {
            b.ToTable("entries");
            b.HasKey(e => e.Id);
            b.Property(e => e.Scope).HasMaxLength(50).IsRequired();
            b.Property(e => e.Name).HasMaxLength(100).IsRequired();
            b.Property(e => e.EncryptedValue).IsRequired();
            b.Property(e => e.Iv).HasMaxLength(24).IsRequired();
            b.Property(e => e.Tag).HasMaxLength(24).IsRequired();

            b.HasIndex(e => new { e.TenantId, e.Scope, e.EntityId, e.Name })
                .IsUnique()
                .HasDatabaseName("ux_secrets_tenant_scope_entity_name");
        });
    }
}
