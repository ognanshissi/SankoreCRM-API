namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class LeadConsentConfiguration : IEntityTypeConfiguration<LeadConsent>
{
    public void Configure(EntityTypeBuilder<LeadConsent> builder)
    {
        builder.ToTable("lead_consents");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Type)
               .HasConversion<string>()
               .HasMaxLength(50);

        builder.Property(c => c.Channel)
               .HasConversion<string>()
               .HasMaxLength(30);

        builder.Property(c => c.Status)
               .HasConversion<string>()
               .HasMaxLength(20);

        builder.Property(c => c.ProofReference).HasMaxLength(1000);
        builder.Property(c => c.WithdrawalReason).HasMaxLength(500);

        // All consents for a lead (main lookup).
        builder.HasIndex(c => new { c.TenantId, c.LeadId });

        // Active-consent check by type (e.g. "does this lead have active Marketing consent?").
        builder.HasIndex(c => new { c.TenantId, c.LeadId, c.Type })
               .HasFilter("status = 'Active'");
    }
}
