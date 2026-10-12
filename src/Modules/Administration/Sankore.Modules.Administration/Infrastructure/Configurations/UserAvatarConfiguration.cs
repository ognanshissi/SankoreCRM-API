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
        b.Property(a => a.Content).IsRequired();
        b.Property(a => a.ContentType).HasMaxLength(30).IsRequired();
    }
}
