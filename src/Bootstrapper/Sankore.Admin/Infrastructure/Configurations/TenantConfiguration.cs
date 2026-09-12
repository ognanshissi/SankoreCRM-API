using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public class TenantConfiguration: IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.RootUserEmail).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Fqdn)
            .HasMaxLength(253)
            .HasColumnType("citext")
            .IsRequired();

        builder.OwnsOne<SubscriptionStateInfo>(p => p.SubscriptionStateInfo, (x) =>
        {
            x.Property(f => f.NextRenewalAt)
                .IsRequired()
                .HasColumnName("subscription_state_info_next_renewal_at");
            x.Property(s => s.RenewalPeriod)
                .HasColumnName("subscription_state_info_renewal_period")
                .HasConversion(v => v.ToString(), v => Enum.Parse<RenewalPeriod>(v));
        });

        builder.Property(p => p.SubscriptionState)
            .IsRequired()
            .HasConversion(
                v => v.ToString(),
                v => Enum.Parse<SubscriptionState>(v));
        
        builder.HasIndex(x => x.Fqdn).IsUnique();
        builder.HasIndex(p => p.RootUserEmail).IsUnique();
        builder.HasIndex(p => new { p.RootUserEmail, p.Fqdn }).IsUnique();
        builder.HasIndex(p => p.Id);
    }
}