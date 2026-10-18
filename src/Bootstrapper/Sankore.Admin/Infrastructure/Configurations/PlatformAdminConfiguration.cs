using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public sealed class PlatformAdminConfiguration : IEntityTypeConfiguration<PlatformAdmin>
{
    public void Configure(EntityTypeBuilder<PlatformAdmin> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Email).HasMaxLength(256).IsRequired();
        builder.Property(p => p.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.Property(p => p.FullName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.PasswordHash).IsRequired();

        builder.HasIndex(p => p.NormalizedEmail).IsUnique();
    }
}

public sealed class PlatformAdminRefreshTokenConfiguration : IEntityTypeConfiguration<PlatformAdminRefreshToken>
{
    public void Configure(EntityTypeBuilder<PlatformAdminRefreshToken> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.TokenHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(p => p.TokenHash).IsUnique();
        builder.HasIndex(p => p.AdminId);

        builder.HasOne<PlatformAdmin>()
            .WithMany()
            .HasForeignKey(p => p.AdminId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
