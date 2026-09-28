using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class LegalFormConfiguration : IEntityTypeConfiguration<LegalForm>
{
    public void Configure(EntityTypeBuilder<LegalForm> builder)
    {
        builder.ToTable("legal_forms");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.Code).HasMaxLength(30).IsRequired();
        builder.Property(f => f.Label).HasMaxLength(200).IsRequired();

        // Client.LegalFormCode is validated against this closed list
        // (LEGAL_FORM_UNKNOWN); the code is the natural key per tenant.
        builder.HasIndex(f => new { f.TenantId, f.Code })
            .IsUnique()
            .HasDatabaseName("ux_legal_forms_code");
    }
}
