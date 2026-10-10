using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

public class ProductSpecialityConfiguration : IEntityTypeConfiguration<ProductSpeciality>
{
    public void Configure(EntityTypeBuilder<ProductSpeciality> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(150).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.Category)
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.ParametersJson)
            .HasColumnType("jsonb")
            .HasColumnName("parameters");
        builder.Property(x => x.BusinessProductId).HasMaxLength(100);
        builder.Property(x => x.BusinessPlatformName).HasMaxLength(100);

        // BNF-B-06. Typed jsonb: M13 and M01 read the policy whole and never filter on a field
        // inside it — the one rule they DO query, AllowMultipleHoldings, is a column instead.
        //
        // The DEFAULT is load-bearing and must be a valid json document. Left to EF, a non-nullable
        // converted property generates `DEFAULT ''` — which PostgreSQL rejects outright on a jsonb
        // column ("invalid input syntax for type json"), so the migration fails rather than the
        // data being wrong. `{}` deserialises to BeneficiaryPolicy.None, since every member of the
        // record is optional, which is exactly what an unconfigured line should carry.
        builder.Property(x => x.BeneficiaryPolicy)
            .HasColumnType("jsonb")
            .HasColumnName("beneficiary_policy")
            .HasConversion(new BeneficiaryPolicyConverter())
            .HasDefaultValueSql("'{}'::jsonb")
            .Metadata.SetValueComparer(new BeneficiaryPolicyComparer());

        builder.Property(x => x.AllowMultipleHoldings).HasDefaultValue(true);

        builder.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        builder.HasIndex(x => new { x.TenantId, x.IsActive });
    }
}
