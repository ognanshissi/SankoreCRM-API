namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycIdentityDocumentConfiguration : IEntityTypeConfiguration<KycIdentityDocument>
{
    public void Configure(EntityTypeBuilder<KycIdentityDocument> b)
    {
        b.ToTable("kyc_identity_documents");
        b.HasKey(d => d.Id);

        b.Property(d => d.DocType).HasMaxLength(40).IsRequired();
        b.Property(d => d.EncryptedNumber).IsRequired();
        b.Property(d => d.NumberBlindIndex).HasMaxLength(64).IsRequired();
        b.Property(d => d.IssuingCountry).HasMaxLength(3);
        b.Property(d => d.ServiceVersion).HasMaxLength(50);

        // jsonb, not text: these are queried for a single field during an investigation, and a
        // text column would force a full scan plus a parse per row.
        b.Property(d => d.OcrFieldsJson).HasColumnType("jsonb");
        b.Property(d => d.OcrFieldConfidencesJson).HasColumnType("jsonb");
        b.Property(d => d.MrzDataJson).HasColumnType("jsonb");

        // TEXT and not jsonb, unlike its three neighbours: the value is an AES-GCM payload
        // ("v1:nonce:tag:ciphertext"), so Postgres would reject it as JSON — and there is nothing
        // to query inside it, which is the whole point of encrypting it.
        b.Property(d => d.EncryptedOcrPayload);

        // THE index duplicate detection runs on. Not unique: the same person legitimately appears
        // on a rejected file and a new one, and uniqueness would block re-enrolment after a
        // refusal. The rule is a flag plus a compliance decision, not a constraint.
        b.HasIndex(d => new { d.TenantId, d.NumberBlindIndex })
            .HasDatabaseName("ix_kyc_identity_documents_blind_index");

        b.HasIndex(d => d.KycFileId)
            .HasDatabaseName("ix_kyc_identity_documents_file");

        b.Ignore(d => d.DomainEvents);
    }
}

internal sealed class KycFaceVerificationConfiguration : IEntityTypeConfiguration<KycFaceVerification>
{
    public void Configure(EntityTypeBuilder<KycFaceVerification> b)
    {
        b.ToTable("kyc_face_verifications");
        b.HasKey(v => v.Id);

        b.Property(v => v.SelfieStorageRef).HasMaxLength(200);
        b.Property(v => v.ModelVersion).HasMaxLength(50);
        b.Property(v => v.QualityScoresJson).HasColumnType("jsonb");

        // Ciphertext, so text — see EncryptedOcrPayload.
        b.Property(v => v.EncryptedFacePayload);

        b.HasIndex(v => new { v.KycFileId, v.Attempt })
            .IsUnique()
            .HasDatabaseName("ux_kyc_face_verifications_file_attempt");

        b.Ignore(v => v.DomainEvents);
    }
}

internal sealed class KycConfidenceAssessmentConfiguration : IEntityTypeConfiguration<KycConfidenceAssessment>
{
    public void Configure(EntityTypeBuilder<KycConfidenceAssessment> b)
    {
        b.ToTable("kyc_confidence_assessments");
        b.HasKey(a => a.Id);

        b.Property(a => a.Level).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(a => a.Trigger).HasMaxLength(40).IsRequired();
        b.Property(a => a.ServiceVersion).HasMaxLength(50);
        b.Property(a => a.BreakdownJson).HasColumnType("jsonb");
        b.Property(a => a.FlagsJson).HasColumnType("jsonb");

        b.HasIndex(a => new { a.KycFileId, a.CreatedAt })
            .HasDatabaseName("ix_kyc_confidence_assessments_file");

        b.Ignore(a => a.DomainEvents);
    }
}

internal sealed class KycFieldCorrectionConfiguration : IEntityTypeConfiguration<KycFieldCorrection>
{
    public void Configure(EntityTypeBuilder<KycFieldCorrection> b)
    {
        b.ToTable("kyc_field_corrections");
        b.HasKey(c => c.Id);

        b.Property(c => c.FieldName).HasMaxLength(100).IsRequired();
        b.Property(c => c.Source).HasMaxLength(8).IsRequired();
        b.Property(c => c.EncryptedNewValue).IsRequired();

        b.HasIndex(c => new { c.KycFileId, c.CorrectedAt })
            .HasDatabaseName("ix_kyc_field_corrections_file");

        b.Ignore(c => c.DomainEvents);
    }
}
