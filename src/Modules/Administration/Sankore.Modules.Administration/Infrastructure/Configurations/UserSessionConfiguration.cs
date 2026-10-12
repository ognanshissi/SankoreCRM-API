using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions");
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.IpAddress).HasMaxLength(UserLoginLocation.MaxIpLength);
        b.Property(s => s.Browser).HasMaxLength(UserLoginLocation.MaxBrowserLength);
        b.Property(s => s.Platform).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.ClientKind).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.EndReason).HasConversion<string>().HasMaxLength(40);
        b.HasIndex(s => new { s.TenantId, s.UserId, s.RevokedAt });
        b.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
