using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class ProductSpecialityConfiguration: IEntityTypeConfiguration<ProductSpeciality>
{
    public void Configure(EntityTypeBuilder<ProductSpeciality> builder)
    {
        builder.HasKey(x => x.Id);
        
        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        
        builder.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
    }
}