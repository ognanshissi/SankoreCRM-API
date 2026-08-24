using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class AgencyConfiguration: IEntityTypeConfiguration<Agency>
{
    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        builder.ToTable("agencies");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TenantId).IsRequired();
        builder.Property(x => x.AgencyType)
            .HasConversion(
                v => v.ToString(),
                v => Enum.Parse<AgencyType>(v))
            .IsRequired();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.OwnsOne(x => x.Address, a =>
        {
            a.Property(x => x.Street).HasColumnName("address_street");
            a.Property(x => x.City).HasColumnName("address_city");
            a.Property(x => x.State).HasColumnName("address_state");
            a.Property(x => x.Country).HasColumnName("address_country");
            a.Property(x => x.ZipCode).HasColumnName("address_zipcode");
            // GeoPoint is an owned type (no PostGIS required); stored as two double columns.
            a.OwnsOne(x => x.Location, loc =>
            {
                loc.Property(l => l.Latitude).HasColumnName("address_location_lat");
                loc.Property(l => l.Longitude).HasColumnName("address_location_lng");
            });
        });
        
        builder.HasOne<Agency>()
            .WithMany()
            .HasForeignKey(a => a.ParentAgencyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.IsActive).HasDefaultValue(true);
        builder.Property(x => x.IsDeleted).HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).IsRequired();

        builder.HasMany(a => a.Users)
            .WithOne(a => a.Agency)
            .HasForeignKey(a => a.AgencyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.ParentAgencyId });
        builder.HasIndex(x => new { x.TenantId, x.IsDeleted });
    }
}