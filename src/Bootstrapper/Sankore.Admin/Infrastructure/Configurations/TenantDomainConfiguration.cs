using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public sealed class TenantDomainConfiguration: IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> builder)
    {
        builder.HasKey(t => t.Id);

        // Fqdn stored as citext so lookups are case-insensitive at the DB level
        // without needing a function index. Matches the plan's recommendation.
        builder.Property(t => t.Fqdn)
            .IsRequired()
            .HasMaxLength(253)        // RFC 1035 max FQDN length
            .HasColumnType("citext");

        // Uniqueness enforced at DB level — one row per distinct domain name
        builder.HasIndex(t => t.Fqdn)
            .IsUnique()
            .HasDatabaseName("ix_tenant_domains_fqdn");

        // Logical reference only — no physical FK so tenants can be managed
        // independently (cross-service, different schema in future).
        builder.HasIndex(t => t.TenantId)
            .HasDatabaseName("ix_tenant_domains_tenant_id");

        builder.HasIndex(t => new { t.TenantId, t.IsPrimary })
            .HasDatabaseName("ix_tenant_domains_tenant_primary");

        builder.Property(t => t.ValidFrom).IsRequired();
        builder.Property(t => t.ValidTo).IsRequired(false);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
    }
}
