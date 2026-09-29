namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

internal sealed class ClientImportJobConfiguration : IEntityTypeConfiguration<ClientImportJob>
{
    public void Configure(EntityTypeBuilder<ClientImportJob> builder)
    {
        builder.ToTable("client_import_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.SourceType).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(j => j.SourceReference).HasMaxLength(500).IsRequired();
        builder.Property(j => j.OriginalFileName).HasMaxLength(300);
        builder.Property(j => j.ErrorMessage).HasMaxLength(2000);
        builder.Property(j => j.FailureDetailsJson).HasColumnType("jsonb");

        builder.HasIndex(j => new { j.TenantId, j.CreatedAt });
        builder.HasIndex(j => new { j.TenantId, j.InitiatedBy });
    }
}
