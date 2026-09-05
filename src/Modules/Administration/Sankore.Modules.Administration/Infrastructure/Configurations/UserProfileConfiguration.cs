using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class UserProfileConfiguration: IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.ToTable("user_profile");
        builder.HasKey(x => x.Id);
        builder.Property(profile => profile.Id).HasMaxLength(100).HasColumnName("additional_email");
        
        builder.HasOne(x => x.User)
            .WithOne(x => x.Profile)
            .HasForeignKey<UserProfile>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.OwnsOne(u => u.Address, a =>
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

        builder.OwnsOne(p => p.WorkNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).HasColumnName("work_number_confirmed_at");
            w.Property(x => x.Contact).HasColumnName("work_number_contact");
        });
        
        builder.OwnsOne(p => p.HomeNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).HasColumnName("home_number_confirmed_at");
            w.Property(x => x.Contact).HasColumnName("home_number_contact");
        });
        
        builder.OwnsOne(p => p.PersonalNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).HasColumnName("personal_number_confirmed_at");
            w.Property(x => x.Contact).HasColumnName("personal_number_contact");
        });
        
        builder.HasIndex(p => p.UserId).IsUnique();
    }
}