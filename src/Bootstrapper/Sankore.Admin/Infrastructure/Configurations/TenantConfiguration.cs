using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public class TenantConfiguration: IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        // je t'aime ambroise 
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Fqdn).IsRequired();

        builder.HasIndex(x => x.Fqdn).IsUnique();
        builder.HasIndex(p => p.Id);
    }
}