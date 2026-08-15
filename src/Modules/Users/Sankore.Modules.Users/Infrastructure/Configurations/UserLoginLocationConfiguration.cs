using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users.Infrastructure.Configurations;

public class UserLoginLocationConfiguration: IEntityTypeConfiguration<UserLoginLocation>
{
    public void Configure(EntityTypeBuilder<UserLoginLocation> builder)
    {
        builder.ToTable("user_login_locations");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
        builder.OwnsOne(x => x.Location, loc =>
        {
            loc.Property(l => l.Latitude).HasColumnName("lat");
            loc.Property(l => l.Longitude).HasColumnName("lng");
        });
    }
}