using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientContactPointConfiguration : IEntityTypeConfiguration<ClientContactPoint>
{
    public void Configure(EntityTypeBuilder<ClientContactPoint> builder)
    {
        builder.ToTable("client_contact_points");
        builder.HasKey(cp => cp.Id);

        builder.Property(cp => cp.TenantId).IsRequired();
        builder.Property(cp => cp.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(cp => cp.EncryptedValue).HasMaxLength(2000).IsRequired();
        builder.Property(cp => cp.BlindIndex).HasMaxLength(64).IsRequired();
        builder.Property(cp => cp.Label).HasMaxLength(100);

        // Lookup by blind index: "which client owns this phone number?" without
        // ever decrypting a single row.
        builder.HasIndex(cp => new { cp.TenantId, cp.Type, cp.BlindIndex });
        builder.HasIndex(cp => new { cp.TenantId, cp.ClientId, cp.Type });

        // One primary contact per client per type, enforced by the database.
        // The filter scopes it to live rows: a closed contact point keeps its
        // historical is_primary flag without blocking the new one.
        builder.HasIndex(cp => new { cp.TenantId, cp.ClientId, cp.Type })
            .IsUnique()
            .HasFilter("is_primary = true AND valid_to IS NULL")
            .HasDatabaseName("ux_client_contact_points_primary");

        builder.Ignore(cp => cp.IsActive);
    }
}
