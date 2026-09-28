using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class SensitiveDataAccessLogConfiguration : IEntityTypeConfiguration<SensitiveDataAccessLog>
{
    public void Configure(EntityTypeBuilder<SensitiveDataAccessLog> builder)
    {
        builder.ToTable("sensitive_data_access_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.TenantId).IsRequired();
        // Field NAME only — this table records who looked, never what they saw.
        builder.Property(l => l.FieldName).HasMaxLength(60).IsRequired();
        builder.Property(l => l.CorrelationId).HasMaxLength(100);

        // Drives the rolling reveal-rate check (REVEAL_RATE_LIMIT_EXCEEDED): count
        // an actor's reveals since a timestamp, so actor + time lead the index.
        builder.HasIndex(l => new { l.TenantId, l.ActorUserId, l.AccessedAt });
        builder.HasIndex(l => new { l.TenantId, l.ClientId });
    }
}
