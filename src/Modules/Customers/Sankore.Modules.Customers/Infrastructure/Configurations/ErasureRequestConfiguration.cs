using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ErasureRequestConfiguration : IEntityTypeConfiguration<ErasureRequest>
{
    public void Configure(EntityTypeBuilder<ErasureRequest> builder)
    {
        builder.ToTable("erasure_requests");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.SubjectType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(r => r.SubjectReference).HasMaxLength(60).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(ErasureRequest.MaxTextLength).IsRequired();
        builder.Property(r => r.Evidence).HasMaxLength(ErasureRequest.MaxTextLength).IsRequired();
        builder.Property(r => r.DecisionNote).HasMaxLength(ErasureRequest.MaxTextLength);
        builder.Property(r => r.Blocker).HasMaxLength(60);

        // The queue screen (pending first) and the daily job (restricted ones).
        builder.HasIndex(r => new { r.TenantId, r.Status, r.RequestedAt });

        // One open request per person: a second click must not create a second approval to give.
        builder.HasIndex(r => new { r.TenantId, r.SubjectType, r.SubjectId })
            .IsUnique()
            .HasFilter("status IN ('Pending', 'Restricted')")
            .HasDatabaseName("ux_erasure_requests_open_subject");
    }
}
