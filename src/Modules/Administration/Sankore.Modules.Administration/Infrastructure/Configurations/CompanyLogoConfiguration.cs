using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class CompanyLogoConfiguration : IEntityTypeConfiguration<CompanyLogo>
{
    public void Configure(EntityTypeBuilder<CompanyLogo> b)
    {
        b.ToTable("company_logos");
        // Un logo par tenant : la clé EST le tenant.
        b.HasKey(l => l.TenantId);
        b.Property(l => l.TenantId).ValueGeneratedNever();
        // Ancien stockage en base : null une fois l'image dans le stockage objet (2026-11-13).
        b.Property(l => l.Content);
        b.Property(l => l.StorageRef).HasMaxLength(60);
        b.Property(l => l.ContentType).HasMaxLength(30).IsRequired();
    }
}
