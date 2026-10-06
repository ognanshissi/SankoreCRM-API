namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycDocumentConfiguration : IEntityTypeConfiguration<KycDocument>
{
    public void Configure(EntityTypeBuilder<KycDocument> b)
    {
        b.ToTable("kyc_documents");
        b.HasKey(d => d.Id);

        // Strings, like every other enum in this schema: the table is read in psql during an audit,
        // and an integer would make the kind and the verdict unreadable there.
        b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(d => d.ReviewDecision).HasConversion<string>().HasMaxLength(16).IsRequired();

        // 200 matches kyc_document_access_logs.storage_ref and kyc_face_verifications
        // .selfie_storage_ref — the same opaque reference, so the same width.
        b.Property(d => d.StorageRef).HasMaxLength(200).IsRequired();
        b.Property(d => d.ContentType).HasMaxLength(100).IsRequired();

        // Hex of SHA-256: 64 characters, fixed.
        b.Property(d => d.Sha256).HasMaxLength(64).IsRequired();

        // Same width as KycApprovalStep.Comment, which is the same kind of value: an operator's
        // motive. A longer one would be silently truncated, so the validator caps it too.
        b.Property(d => d.RefusalReason).HasMaxLength(2000);

        // Maps onto PostgreSQL's xmin system column, exactly as KycFile.Version does. See
        // KycDocument.Version for why this row needs a token and KycApprovalStep does not have one.
        //
        // The migration must NOT declare the column: xmin exists on every table already, so a
        // CREATE TABLE naming it fails with "column name \"xmin\" conflicts with a system column
        // name". EF scaffolds it anyway — see the comment in 20261001113109_InitialKyc.
        b.Property(d => d.Version).IsRowVersion();

        // The one question this table is asked: "the documents of this file, newest kind-by-kind
        // first" — which is how the current front, back and selfie are resolved.
        b.HasIndex(d => new { d.TenantId, d.KycFileId, d.Kind, d.UploadedAt })
            .HasDatabaseName("ix_kyc_documents_file_kind");

        // Not unique on StorageRef as a constraint across tenants — the store already guarantees
        // uniqueness by construction (16 random bytes) and a unique index here would turn a
        // collision into a 500 on upload instead of what it is: impossible.
        b.HasIndex(d => new { d.TenantId, d.StorageRef })
            .HasDatabaseName("ix_kyc_documents_storage_ref");

        b.Ignore(d => d.DomainEvents);
    }
}
