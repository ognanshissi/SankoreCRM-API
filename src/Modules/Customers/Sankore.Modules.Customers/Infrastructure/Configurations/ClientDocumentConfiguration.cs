using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientDocumentConfiguration : IEntityTypeConfiguration<ClientDocument>
{
    public void Configure(EntityTypeBuilder<ClientDocument> builder)
    {
        builder.ToTable("client_documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.TenantId).IsRequired();
        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(d => d.Title).HasMaxLength(ClientDocument.TitleMaxLength).IsRequired();
        builder.Property(d => d.Direction).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.FileName).HasMaxLength(255).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(d => d.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(d => d.StorageRef).HasMaxLength(100).IsRequired();
        builder.Property(d => d.RemovalReason).HasMaxLength(ClientDocument.ReasonMaxLength);

        builder.HasIndex(d => new { d.TenantId, d.ClientId });
    }
}
