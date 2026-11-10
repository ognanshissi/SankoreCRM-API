using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class CompanyInfoConfiguration: IEntityTypeConfiguration<CompanyInfo>
{
    public void Configure(EntityTypeBuilder<CompanyInfo> builder)
    {
        builder.ToTable("company_info");
        builder.Property(b => b.Name).HasMaxLength(150).IsRequired();
        builder.Property(b => b.Description).HasMaxLength(200);

        builder.Property(x => x.DefaultLanguage)
            .HasConversion(
                v => v.ToString(),
                v => Enum.Parse<Languages>(v))
            .IsRequired();
        
        builder.Property(b => b.PrimaryColor).HasMaxLength(10);
        builder.Property(b => b.SecondaryColor).HasMaxLength(10);
        builder.Property(b => b.Currency).HasMaxLength(3).IsRequired().HasDefaultValue(InstitutionCurrencies.Default);

        // Quote lines, lot 1 (2026-11-10). No model default on purpose: EF skips a value equal to the
        // CLR default (0) on insert when a column has one, so a 0 % rate would be stored as the
        // default. Existing rows got 18 and 9 from the migration (AddPricingAndVat), new rows from
        // the entity's initialisers.
        builder.Property(b => b.VatStandardRate).HasPrecision(5, 2);
        builder.Property(b => b.VatReducedRate).HasPrecision(5, 2);
        builder.Property(b => b.MaxDiscountPercent).HasPrecision(5, 2);

        // Legal identity and quote settings (quote lines, lot 2).
        builder.Property(b => b.LegalName).HasMaxLength(Domain.CompanyInfo.MaxLegalNameLength);
        builder.Property(b => b.LegalForm).HasMaxLength(Domain.CompanyInfo.MaxLegalFormLength);
        builder.Property(b => b.HeadOfficeAddress).HasMaxLength(Domain.CompanyInfo.MaxHeadOfficeAddressLength);
        builder.Property(b => b.LegalPhone).HasMaxLength(Domain.CompanyInfo.MaxLegalPhoneLength);
        builder.Property(b => b.LegalEmail).HasMaxLength(Domain.CompanyInfo.MaxLegalEmailLength);
        builder.Property(b => b.Website).HasMaxLength(Domain.CompanyInfo.MaxWebsiteLength);
        builder.Property(b => b.Rccm).HasMaxLength(Domain.CompanyInfo.MaxRccmLength);
        builder.Property(b => b.TaxpayerNumber).HasMaxLength(Domain.CompanyInfo.MaxTaxpayerNumberLength);
        builder.Property(b => b.TaxRegime).HasMaxLength(Domain.CompanyInfo.MaxTaxRegimeLength);
        builder.Property(b => b.QuoteFooter).HasMaxLength(Domain.CompanyInfo.MaxQuoteFooterLength);
        // No model default, as for the VAT rates: existing rows get DEV / 30 from the migration.
        builder.Property(b => b.QuotePrefix).HasMaxLength(Domain.CompanyInfo.MaxQuotePrefixLength).IsRequired();
        builder.Property(b => b.QuotePaymentTerms).HasMaxLength(Domain.CompanyInfo.MaxPaymentTermsLength);

        builder.HasIndex(b => b.TenantId);

        builder.HasIndex(b => new { b.TenantId, b.Id }).IsUnique();
    }
}