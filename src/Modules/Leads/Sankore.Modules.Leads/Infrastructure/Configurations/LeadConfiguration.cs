using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

namespace Sankore.Modules.Leads.Infrastructure.Configurations;

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> builder)
    {
        builder.ToTable("leads");

        builder.HasKey(l => l.Id);

        // Required scalars
        builder.Property(l => l.FullName).HasMaxLength(200).IsRequired();
        builder.Property(l => l.PhoneNumber).HasMaxLength(30).IsRequired();
        builder.Property(l => l.PhoneBlindIndex).HasMaxLength(64);
        builder.Property(l => l.InterestedProduct).HasMaxLength(100).IsRequired();
        builder.Property(l => l.PreferredLanguage).HasMaxLength(50).IsRequired();

        // Optional identity
        builder.Property(l => l.FirstName).HasMaxLength(100);
        builder.Property(l => l.LastName).HasMaxLength(100);
        builder.Property(l => l.Email).HasMaxLength(200);

        // Organisation
        builder.Property(l => l.CompanyName).HasMaxLength(200);
        builder.Property(l => l.CompanyEmail).HasMaxLength(200);
        builder.Property(l => l.CompanyPhone).HasMaxLength(30);
        builder.Property(l => l.Website).HasMaxLength(300).IsRequired(false);

        // Enums stored as strings
        builder.Property(l => l.ProspectType).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.Source).HasConversion<string>().HasMaxLength(30);
        builder.Property(l => l.PipelineStage).HasConversion<string>().HasMaxLength(40);
        builder.Property(l => l.Gender).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.IntentLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.Channel).HasConversion<string>().HasMaxLength(30);

        // Identity fields for duplicate detection
        builder.Property(l => l.NationalId).HasMaxLength(50);
        builder.Property(l => l.CustomerReference).HasMaxLength(100);
        builder.HasIndex(l => new { l.TenantId, l.NationalId })
               .HasFilter("national_id IS NOT NULL");
        builder.HasIndex(l => new { l.TenantId, l.CustomerReference })
               .HasFilter("customer_reference IS NOT NULL");

        // Capture context
        builder.Property(l => l.Campaign).HasMaxLength(100);
        builder.Property(l => l.ExternalReference).HasMaxLength(100);
        builder.Property(l => l.Comment).HasMaxLength(2000);
        builder.Property(l => l.LossReason).HasMaxLength(500);

        // Money owned type
        builder.OwnsOne(l => l.DesiredAmount, m =>
        {
            m.Property(p => p.Amount)
                .HasColumnName("desired_amount")
                .HasPrecision(18, 4);
            m.Property(p => p.Currency)
                .HasColumnName("desired_currency")
                .HasMaxLength(3);
        });

        // Acquisition cost — snapshot from source at capture time
        builder.OwnsOne(l => l.AcquisitionCost, m =>
        {
            m.Property(p => p.Amount).HasColumnName("acquisition_cost").HasPrecision(18, 4);
            m.Property(p => p.Currency).HasColumnName("acquisition_currency").HasMaxLength(3);
        });

        // Attribution & UTM
        builder.Property(l => l.UtmSource).HasMaxLength(200);
        builder.Property(l => l.UtmMedium).HasMaxLength(100);
        builder.Property(l => l.UtmCampaign).HasMaxLength(200);
        builder.Property(l => l.UtmContent).HasMaxLength(200);
        builder.Property(l => l.UtmTerm).HasMaxLength(200);
        builder.Property(l => l.LandingPage).HasMaxLength(2000);
        builder.Property(l => l.Referrer).HasMaxLength(2000);
        builder.Property(l => l.ExternalId).HasMaxLength(200);
        builder.Property(l => l.SocialPublicationId).HasMaxLength(200);
        builder.Property(l => l.SocialInteractionId).HasMaxLength(200);

        // Owned value object
        builder.OwnsOne(l => l.Location, loc =>
        {
            loc.Property(p => p.Latitude).HasColumnName("lat");
            loc.Property(p => p.Longitude).HasColumnName("lng");
        });

        // Indexes
        builder.HasIndex(l => new { l.TenantId, l.Status });
        builder.HasIndex(l => new { l.TenantId, l.PipelineStage });
        builder.HasIndex(l => l.PhoneNumber);
        builder.HasIndex(l => new { l.TenantId, l.PhoneBlindIndex })
               .HasFilter("phone_blind_index IS NOT NULL");
        builder.HasIndex(l => l.FullName);
        builder.HasIndex(l => l.ExpiresAt);

        // Domain events are transient, never persisted.
        builder.Ignore(l => l.DomainEvents);
    }
}
