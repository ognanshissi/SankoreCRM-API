using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class PermissionAttributionConfiguration: IEntityTypeConfiguration<PermissionAttribution>
{
    public void Configure(EntityTypeBuilder<PermissionAttribution> builder)
    {
        builder.ToTable("permission_attribution");

        builder.HasOne(u => u.User)
            .WithMany(u => u.PermissionAttributions)
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.HasIndex(a => new  { a.UserId, a.EntityId })
            .IsUnique()
            .HasFilter("\"IsActive\" = true");
    }
}