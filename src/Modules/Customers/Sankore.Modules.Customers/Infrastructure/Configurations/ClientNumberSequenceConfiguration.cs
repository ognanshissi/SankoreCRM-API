using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientNumberSequenceConfiguration : IEntityTypeConfiguration<ClientNumberSequence>
{
    public void Configure(EntityTypeBuilder<ClientNumberSequence> builder)
    {
        builder.ToTable("client_number_sequences");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.AgencyCode).HasMaxLength(50).IsRequired();

        // One counter row per tenant + agency + year. The unique index is what
        // makes the generator's "create the row if absent" path safe under
        // concurrency: the loser of the race gets a unique violation and retries.
        builder.HasIndex(s => new { s.TenantId, s.AgencyCode, s.Year })
            .IsUnique()
            .HasDatabaseName("ux_client_number_sequences");

        builder.Property(s => s.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
