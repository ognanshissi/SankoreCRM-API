using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Users.Domain;

namespace Sankore.Modules.Users.Infrastructure.Configurations;

public class AgencyConfiguration: IEntityTypeConfiguration<Agency>
{
    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.AgencyType).HasConversion<string>().IsRequired();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.OwnsOne(x => x.Address, a =>
        {
            a.Property(x => x.Street).HasColumnName("street");
            a.Property(x => x.City).HasColumnName("city");
            a.Property(x => x.State).HasColumnName("state");
            a.Property(x => x.Country).HasColumnName("country");
            a.Property(x => x.ZipCode).HasColumnName("zipcode");
            // GeoPoint is an owned type (no PostGIS required); stored as two double columns.
            a.OwnsOne(x => x.Location, loc =>
            {
                loc.Property(l => l.Latitude).HasColumnName("lat");
                loc.Property(l => l.Longitude).HasColumnName("lng");
            });
        });
        builder.HasIndex(x => new  { x.TenantId, x.Code }).IsUnique();
        
        builder.HasIndex(x => new { x.TenantId, x.ParentAgencyId});
    }
}