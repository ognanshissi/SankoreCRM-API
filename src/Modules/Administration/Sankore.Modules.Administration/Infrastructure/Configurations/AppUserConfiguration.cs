using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class AppUserConfiguration: IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("app_users");
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();

        // M12 lifecycle columns
        builder.Property(u => u.Status).HasConversion<string>().IsRequired();
        builder.Property(u => u.MfaEnabled).HasDefaultValue(true);
        builder.Property(u => u.PasswordExpiresAt).IsRequired();
        builder.Property(u => u.FailedLoginAttempts).HasDefaultValue(0);
        builder.Property(u => u.LastLoginAt);
        builder.Property(u => u.DeactivatedAt);

        builder.HasOne(u => u.Agency)
            .WithMany(a => a.Users)
            .HasForeignKey(u => u.AgencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.OwnsOne<GeoPoint>(u => u.LastKnownLocation, loc =>
        {
            loc.Property(l => l.Latitude).HasColumnName("lat");
            loc.Property(l => l.Longitude).HasColumnName("lng");
        });

        builder.Property(u => u.SpokenLanguages).HasColumnType("text[]");
        builder.Property(u => u.Specialties).HasColumnType("text[]");

        builder.HasCheckConstraint(
            "CK_User_AgencyId_RequiredForStandard",
            "(\"AccountType\" != 0) OR (\"AgencyId\" IS NOT NULL)"
        );
        
        builder.HasCheckConstraint(
            "CK_User_System_NoAgency",
            "(\"AccountType\" != 1) OR (\"AgencyId\" IS NULL)"
        );
        
        builder.HasIndex(u => new { u.TenantId, u.AgencyId });
        builder.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
    }
}