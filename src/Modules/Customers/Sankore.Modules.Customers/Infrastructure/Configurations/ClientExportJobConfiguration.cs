using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientExportJobConfiguration : IEntityTypeConfiguration<ClientExportJob>
{
    public void Configure(EntityTypeBuilder<ClientExportJob> builder)
    {
        builder.ToTable("client_export_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.TenantId).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(j => j.FiltersJson).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.FileReference).HasMaxLength(500);
        builder.Property(j => j.DownloadToken).HasMaxLength(64).IsRequired();
        builder.Property(j => j.ErrorMessage).HasMaxLength(2000);

        // The token IS the capability to download the file, so it has to be unique
        // tenant-wide; the expiry check stays in the domain (EXPORT_LINK_EXPIRED).
        builder.HasIndex(j => new { j.TenantId, j.DownloadToken })
            .IsUnique()
            .HasDatabaseName("ux_client_export_jobs_token");

        builder.HasIndex(j => new { j.TenantId, j.RequestedBy, j.RequestedAt });
    }
}
