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

        builder.HasIndex(b => b.TenantId);

        builder.HasIndex(b => new { b.TenantId, b.Id }).IsUnique();
    }
}