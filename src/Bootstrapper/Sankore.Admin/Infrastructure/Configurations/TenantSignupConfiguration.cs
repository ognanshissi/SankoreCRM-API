using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public sealed class TenantSignupConfiguration : IEntityTypeConfiguration<TenantSignup>
{
    public void Configure(EntityTypeBuilder<TenantSignup> builder)
    {
        builder.ToTable("TenantSignups");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.InstitutionName).HasMaxLength(TenantSignup.InstitutionNameMaxLength).IsRequired();
        builder.Property(s => s.Slug).HasMaxLength(Sankore.Shared.Kernel.TrialSlug.MaxLength).IsRequired();
        builder.Property(s => s.FirstName).HasMaxLength(TenantSignup.PersonNameMaxLength).IsRequired();
        builder.Property(s => s.LastName).HasMaxLength(TenantSignup.PersonNameMaxLength).IsRequired();
        builder.Property(s => s.Email).HasMaxLength(TenantSignup.EmailMaxLength).HasColumnType("citext").IsRequired();
        builder.Property(s => s.Phone).HasMaxLength(TenantSignup.PhoneMaxLength).IsRequired();
        builder.Property(s => s.Country).HasMaxLength(TenantSignup.CountryMaxLength).IsRequired();
        builder.Property(s => s.ActivityType).HasMaxLength(TenantSignup.ActivityMaxLength).IsRequired();
        builder.Property(s => s.RequestIp).HasMaxLength(TenantSignup.IpMaxLength);
        builder.Property(s => s.UserAgent).HasMaxLength(TenantSignup.UserAgentMaxLength);
        builder.Property(s => s.TokenHash).HasMaxLength(TenantSignup.TokenHashLength).IsRequired();
        builder.Property(s => s.Locale).HasMaxLength(5).IsRequired().HasDefaultValue("fr");
        builder.Property(s => s.RejectionReason).HasMaxLength(TenantSignup.ReasonMaxLength);

        builder.Property(s => s.Status).IsRequired().HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<TenantSignupStatus>(v));

        // The link is looked up by its hash, and one hash names one request.
        builder.HasIndex(s => s.TokenHash).IsUnique().HasDatabaseName("ux_tenant_signups_token_hash");
        builder.HasIndex(s => new { s.Status, s.CreatedAt }).HasDatabaseName("ix_tenant_signups_status_created");
        builder.HasIndex(s => s.Slug).HasDatabaseName("ix_tenant_signups_slug");
        builder.HasIndex(s => s.Email).HasDatabaseName("ix_tenant_signups_email");
        builder.HasIndex(s => s.RequestIp).HasDatabaseName("ix_tenant_signups_request_ip");
        // Logical reference only — no physical FK, like TenantDomains.
        builder.HasIndex(s => s.TenantId).HasDatabaseName("ix_tenant_signups_tenant");
    }
}
