using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class RefreshTokenConfiguration: IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.HasKey(r => r.Id);
        b.Property(r => r.Token).HasMaxLength(128).IsRequired();
        b.HasIndex(r => r.Token).IsUnique();
        b.HasIndex(r => new { r.UserId, r.RevokedAt });
    }
}