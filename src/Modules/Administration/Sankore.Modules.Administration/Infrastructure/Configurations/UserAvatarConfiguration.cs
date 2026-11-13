using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class UserAvatarConfiguration : IEntityTypeConfiguration<UserAvatar>
{
    public void Configure(EntityTypeBuilder<UserAvatar> b)
    {
        b.ToTable("user_avatars");
        b.HasKey(a => a.UserId);
        b.Property(a => a.UserId).ValueGeneratedNever();
        b.HasIndex(a => a.TenantId);
        // Ancien stockage en base : null une fois l'image dans le stockage objet (2026-11-13).
        b.Property(a => a.Content);
        b.Property(a => a.StorageRef).HasMaxLength(60);
        b.Property(a => a.ContentType).HasMaxLength(30).IsRequired();
    }
}
