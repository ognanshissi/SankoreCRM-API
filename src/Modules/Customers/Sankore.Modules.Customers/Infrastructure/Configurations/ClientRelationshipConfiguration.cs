using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientRelationshipConfiguration : IEntityTypeConfiguration<ClientRelationship>
{
    public void Configure(EntityTypeBuilder<ClientRelationship> builder)
    {
        builder.ToTable("client_relationships");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Type).HasConversion<string>().HasMaxLength(30).IsRequired();

        builder.Property(r => r.ExternalFullName).HasMaxLength(150);
        builder.Property(r => r.ExternalPhoneEncrypted).HasMaxLength(2000);
        builder.Property(r => r.ExternalPhoneBlindIndex).HasMaxLength(64);
        builder.Property(r => r.EncryptedExternalDocumentNumber).HasMaxLength(2000);
        builder.Property(r => r.CloseReason).HasMaxLength(1000);

        builder.HasIndex(r => new { r.TenantId, r.ClientId });
        builder.HasIndex(r => new { r.TenantId, r.RelatedClientId });
        builder.HasIndex(r => new { r.TenantId, r.ClientId, r.Type });

        // Both ends live in customers.clients, so FKs are legitimate. Restrict
        // rather than Cascade: relationships are compliance evidence and must
        // never disappear as a side effect of deleting the other party.
        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.ClientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Client>()
            .WithMany()
            .HasForeignKey(r => r.RelatedClientId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(r => r.IsActive);
    }
}
