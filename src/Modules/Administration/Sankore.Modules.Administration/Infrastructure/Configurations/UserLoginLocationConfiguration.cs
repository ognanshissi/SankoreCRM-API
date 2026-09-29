using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class UserLoginLocationConfiguration: IEntityTypeConfiguration<UserLoginLocation>
{
    public void Configure(EntityTypeBuilder<UserLoginLocation> builder)
    {
        builder.Property(l => l.IpAddress).HasMaxLength(Domain.UserLoginLocation.MaxIpLength);
        builder.Property(l => l.UserAgent).HasMaxLength(UserAgentParser.MaxRawLength);
        builder.Property(l => l.Browser).HasMaxLength(Domain.UserLoginLocation.MaxBrowserLength);
        builder.Property(l => l.BrowserVersion).HasMaxLength(Domain.UserLoginLocation.MaxBrowserVersionLength);

        // Stored as text, not as an int: an ops query reading this table should not need the
        // enum definition to know what "Android" means.
        builder.Property(l => l.Platform).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.ClientKind).HasConversion<string>().HasMaxLength(20);

        // "Where else has this address signed in?" is the first question of any incident.
        builder.HasIndex(l => new { l.TenantId, l.IpAddress }).HasFilter("ip_address IS NOT NULL");
        builder.HasIndex(l => new { l.TenantId, l.UserId, l.OccuredAt });

        builder.ToTable("user_login_locations");
        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId);
        
        builder.OwnsOne(x => x.Location, loc =>
        {
            loc.Property(l => l.Latitude).HasColumnName("lat");
            loc.Property(l => l.Longitude).HasColumnName("lng");
        });
    }
}