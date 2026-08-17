using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class AppUserConfiguration: IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_users");
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
        
        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(a => a.AgencyId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.OwnsOne<GeoPoint>(u => u.LastKnownLocation, loc =>
        {
            loc.Property(l => l.Latitude).HasColumnName("lat");
            loc.Property(l => l.Longitude).HasColumnName("lng");
        });

        builder.Property(u => u.SpokenLanguages)
            .HasColumnType("text[]");
        builder.Property(u => u.Specialties)
            .HasColumnType("text[]");

        builder.HasIndex(u => new { u.TenantId, u.AgencyId });
    }
}