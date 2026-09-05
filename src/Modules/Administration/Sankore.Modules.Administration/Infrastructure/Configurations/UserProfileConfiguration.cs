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
            a.Property(x => x.Street).IsRequired(false).HasColumnName("address_street");
            a.Property(x => x.City).IsRequired(false).HasColumnName("address_city");
            a.Property(x => x.State).IsRequired(false).HasColumnName("address_state");
            a.Property(x => x.Country).IsRequired(false).HasColumnName("address_country");
            a.Property(x => x.ZipCode).IsRequired(false).HasColumnName("address_zipcode");
            // GeoPoint is an owned type (no PostGIS required); stored as two double columns.
            a.OwnsOne(x => x.Location, loc =>
            {
                loc.Property(l => l.Latitude).HasColumnName("address_location_lat");
                loc.Property(l => l.Longitude).HasColumnName("address_location_lng");
            });
        });

        builder.OwnsOne(p => p.WorkNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).IsRequired(false).HasColumnName("work_number_confirmed_at");
            w.Property(x => x.Contact).IsRequired(false).HasColumnName("work_number_contact");
            w.Property(x => x.IsPrimary).HasDefaultValue(false).HasColumnName("work_number_is_primary");
        });
        
        builder.OwnsOne(p => p.HomeNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).IsRequired(false).HasColumnName("home_number_confirmed_at");
            w.Property(x => x.Contact).IsRequired(false).HasColumnName("home_number_contact");
            w.Property(x => x.IsPrimary).HasDefaultValue(false).HasColumnName("home_number_is_primary");
        });
        
        builder.OwnsOne(p => p.PersonalNumber, w =>
        {
            w.Property(x => x.ConfirmedAt).IsRequired(false).HasColumnName("personal_number_confirmed_at");
            w.Property(x => x.Contact).IsRequired(false).HasColumnName("personal_number_contact");
            w.Property(x => x.IsPrimary).HasDefaultValue(false).HasColumnName("personal_number_is_primary");
        });
        
        builder.HasIndex(p => p.UserId).IsUnique();
    }
}