namespace Sankore.Modules.Customers.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

internal sealed class ClientPaymentInstrumentConfiguration : IEntityTypeConfiguration<ClientPaymentInstrument>
{
    public void Configure(EntityTypeBuilder<ClientPaymentInstrument> b)
    {
        b.ToTable("client_payment_instruments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(100).IsRequired();
        b.Property(x => x.EncryptedIdentifier).HasMaxLength(2000).IsRequired();
        b.Property(x => x.MaskedIdentifier).HasMaxLength(20).IsRequired();
        b.Property(x => x.HolderName).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.ClientId });
        b.Ignore(x => x.IsActive);
    }
}

internal sealed class SuspicionAlertConfiguration : IEntityTypeConfiguration<SuspicionAlert>
{
    public void Configure(EntityTypeBuilder<SuspicionAlert> b)
    {
        b.ToTable("suspicion_alerts");
        b.HasKey(x => x.Id);
        b.Property(x => x.EncryptedFacts).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(x => x.DeclarationReference).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.Status });
    }
}
