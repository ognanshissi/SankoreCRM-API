namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycDocumentAccessLogConfiguration : IEntityTypeConfiguration<KycDocumentAccessLog>
{
    public void Configure(EntityTypeBuilder<KycDocumentAccessLog> b)
    {
        b.ToTable("kyc_document_access_logs");
        b.HasKey(l => l.Id);

        b.Property(l => l.StorageRef).HasMaxLength(200).IsRequired();
        b.Property(l => l.CorrelationId).HasMaxLength(100);
        b.Property(l => l.Reason).HasMaxLength(500);

        // The two questions this table is asked: everything seen on one file, and everything one
        // user looked at. The second is the one that matters during an investigation.
        b.HasIndex(l => new { l.TenantId, l.KycFileId, l.AccessedAt })
            .HasDatabaseName("ix_kyc_document_access_logs_file");

        b.HasIndex(l => new { l.TenantId, l.ActorUserId, l.AccessedAt })
            .HasDatabaseName("ix_kyc_document_access_logs_actor");
    }
}
