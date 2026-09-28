using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientLoyaltyScoreConfiguration : IEntityTypeConfiguration<ClientLoyaltyScore>
{
    public void Configure(EntityTypeBuilder<ClientLoyaltyScore> builder)
    {
        builder.ToTable("client_loyalty_scores");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        // Per-criterion contributions, kept as jsonb so the UI can explain a score
        // without the module having to version a column per weighting rule.
        builder.Property(s => s.BreakdownJson).HasColumnType("jsonb").IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.ClientId });
        builder.HasIndex(s => new { s.TenantId, s.ClientId, s.ComputedAt });
    }
}
