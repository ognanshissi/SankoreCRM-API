using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class PermissionAttributionConfiguration : IEntityTypeConfiguration<PermissionAttribution>
{
    public void Configure(EntityTypeBuilder<PermissionAttribution> builder)
    {
        builder.ToTable("permission_attributions");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.PermissionCode).HasMaxLength(100).IsRequired();
        builder.Property(a => a.ScopeType).HasMaxLength(50);

        builder.HasOne(a => a.User)
            .WithMany(u => u.PermissionAttributions)
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevents assigning the same permission in the same scope twice to the same user
        // while active. ScopeId nullable is handled by the partial index.
        builder.HasIndex(a => new { a.UserId, a.PermissionCode, a.ScopeId })
            .IsUnique()
            .HasFilter("\"IsActive\" = true");

        builder.HasIndex(a => new { a.TenantId, a.UserId });
    }
}
