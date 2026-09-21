using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class ProductSpecialityConfiguration : IEntityTypeConfiguration<ProductSpeciality>
{
    public void Configure(EntityTypeBuilder<ProductSpeciality> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.Category)
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.ParametersJson)
            .HasColumnType("jsonb")
            .HasColumnName("parameters");
        builder.Property(x => x.BusinessProductId).HasMaxLength(100);
        builder.Property(x => x.BusinessPlatformName).HasMaxLength(100);

        builder.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.IsActive });
    }
}
