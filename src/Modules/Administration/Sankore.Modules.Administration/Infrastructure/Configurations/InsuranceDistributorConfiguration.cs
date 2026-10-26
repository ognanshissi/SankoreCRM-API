using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class InsuranceDistributorProfileConfiguration
    : IEntityTypeConfiguration<InsuranceDistributorProfile>
{
    public void Configure(EntityTypeBuilder<InsuranceDistributorProfile> b)
    {
        b.ToTable("insurance_distributor_profiles");
        b.HasKey(p => p.Id);

        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(p => p.RegistrationNumber).HasMaxLength(100);
        b.Property(p => p.LegalMention).HasMaxLength(2000);
        b.Property(p => p.AnnualSmig).HasPrecision(18, 2);

        // One profile per institution.
        b.HasIndex(p => p.TenantId).IsUnique();
    }
}

internal sealed class DistributorDocumentConfiguration
    : IEntityTypeConfiguration<DistributorDocument>
{
    public void Configure(EntityTypeBuilder<DistributorDocument> b)
    {
        b.ToTable("insurance_distributor_documents");
        b.HasKey(d => d.Id);

        b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(d => d.Reference).HasMaxLength(200).IsRequired();
        b.Property(d => d.Issuer).HasMaxLength(200);
        b.Property(d => d.FileReference).HasMaxLength(100);
        b.Property(d => d.FileName).HasMaxLength(255);
        b.Property(d => d.ContentType).HasMaxLength(100);

        b.HasIndex(d => new { d.TenantId, d.Kind, d.SupersededAt });

        b.Ignore(d => d.IsCurrent);
    }
}
