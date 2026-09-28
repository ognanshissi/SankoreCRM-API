using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Infrastructure.Configurations;

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("clients");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();

        // ── Identity & routing ──────────────────────────────────────────────
        builder.Property(c => c.ClientNumber).HasMaxLength(40).IsRequired();
        builder.Property(c => c.AgencyCode).HasMaxLength(50).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(150).IsRequired();
        // Upper-cased, accent-free, surname-first form maintained by SearchKeyBuilder.
        // Wide enough to concatenate every name part of a legal entity.
        builder.Property(c => c.SearchKey).HasMaxLength(320).IsRequired();

        // ── Enums as strings: readable in the database and immune to
        //    renumbering when a new member is inserted in the middle. ────────
        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(c => c.Gender).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.MaritalStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.IdentityDocumentType).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.KycStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(c => c.RiskLevel).HasConversion<string>().HasMaxLength(30).IsRequired();

        // ── Individual identity ─────────────────────────────────────────────
        builder.Property(c => c.FirstName).HasMaxLength(150);
        builder.Property(c => c.LastName).HasMaxLength(150);
        builder.Property(c => c.MaidenName).HasMaxLength(150);
        builder.Property(c => c.BirthPlace).HasMaxLength(150);
        builder.Property(c => c.Nationality).HasMaxLength(100);
        builder.Property(c => c.FatherName).HasMaxLength(150);
        builder.Property(c => c.MotherName).HasMaxLength(150);
        builder.Property(c => c.Profession).HasMaxLength(150);
        builder.Property(c => c.Employer).HasMaxLength(150);
        builder.Property(c => c.PreferredLanguage).HasMaxLength(10).IsRequired();
        builder.Property(c => c.DeclaredIncomeCurrency).HasMaxLength(3);

        // ── Legal entity identity ───────────────────────────────────────────
        builder.Property(c => c.LegalName).HasMaxLength(150);
        builder.Property(c => c.LegalFormCode).HasMaxLength(30);

        // ── Protected fields ────────────────────────────────────────────────
        // Encrypted values are AES-256-GCM base64 envelopes ("v1:nonce:tag:ct"),
        // wider than the clear value they replace; blind indexes are HMAC-SHA256
        // hex, always exactly 64 characters.
        builder.Property(c => c.EncryptedDateOfBirth).HasMaxLength(2000);
        builder.Property(c => c.EncryptedDeclaredIncome).HasMaxLength(2000);
        builder.Property(c => c.EncryptedIdentityDocumentNumber).HasMaxLength(2000);
        builder.Property(c => c.EncryptedRegistrationNumber).HasMaxLength(2000);
        builder.Property(c => c.EncryptedTaxIdNumber).HasMaxLength(2000);
        builder.Property(c => c.DateOfBirthBlindIndex).HasMaxLength(64);
        builder.Property(c => c.IdentityDocumentNumberBlindIndex).HasMaxLength(64);
        builder.Property(c => c.RegistrationNumberBlindIndex).HasMaxLength(64);

        // ── Fuzzy matching ──────────────────────────────────────────────────
        builder.Property(c => c.PhoneticKeyPrimary).HasMaxLength(32);
        builder.Property(c => c.PhoneticKeySecondary).HasMaxLength(32);

        // ── KYC / risk / segmentation ───────────────────────────────────────
        builder.Property(c => c.KycRejectionReason).HasMaxLength(1000);
        builder.Property(c => c.SegmentCode).HasMaxLength(30);
        builder.Property(c => c.ArchiveReason).HasMaxLength(1000);

        // ── Optimistic concurrency on PostgreSQL's system column ────────────
        // No extra column to maintain and no chance of a lost update: the value
        // is bumped by the engine itself on every physical row version.
        builder.Property(c => c.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // ── Child collections owned by the aggregate ─────────────────────────
        // Exposed as IReadOnlyCollection, so EF must read and write the private
        // backing field rather than the property.
        builder.HasMany(c => c.ContactPoints)
            .WithOne()
            .HasForeignKey(cp => cp.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        var contactPoints = builder.Navigation(c => c.ContactPoints);
        contactPoints.HasField("_contactPoints");
        contactPoints.Metadata.SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.StatusHistory)
            .WithOne()
            .HasForeignKey(h => h.ClientId)
            .OnDelete(DeleteBehavior.Cascade);
        var statusHistory = builder.Navigation(c => c.StatusHistory);
        statusHistory.HasField("_statusHistory");
        statusHistory.Metadata.SetPropertyAccessMode(PropertyAccessMode.Field);

        // ── Indexes ─────────────────────────────────────────────────────────
        builder.HasIndex(c => new { c.TenantId, c.ClientNumber })
            .IsUnique()
            .HasDatabaseName("ux_clients_number");

        // Duplicate identity documents are a hard error, not a warning — but only
        // among clients that actually carry one, hence the partial index.
        builder.HasIndex(c => new { c.TenantId, c.IdentityDocumentNumberBlindIndex })
            .IsUnique()
            .HasFilter("identity_document_number_blind_index IS NOT NULL")
            .HasDatabaseName("ux_clients_identity_doc");

        builder.HasIndex(c => new { c.TenantId, c.RegistrationNumberBlindIndex })
            .IsUnique()
            .HasFilter("registration_number_blind_index IS NOT NULL")
            .HasDatabaseName("ux_clients_registration");

        // Lead conversion idempotency (US-M01-BE-06): the database — not the
        // handler — guarantees that one lead can only ever produce one client.
        // A concurrent second conversion attempt loses on this index instead of
        // silently creating a duplicate customer.
        builder.HasIndex(c => new { c.TenantId, c.SourceLeadId })
            .IsUnique()
            .HasFilter("source_lead_id IS NOT NULL")
            .HasDatabaseName("ux_clients_source_lead");

        builder.HasIndex(c => new { c.TenantId, c.Status });
        builder.HasIndex(c => new { c.TenantId, c.AgencyId });
        builder.HasIndex(c => new { c.TenantId, c.AdvisorUserId });
        builder.HasIndex(c => new { c.TenantId, c.PhoneticKeyPrimary });
        builder.HasIndex(c => new { c.TenantId, c.DateOfBirthBlindIndex });
        builder.HasIndex(c => new { c.TenantId, c.SegmentCode });
        builder.HasIndex(c => new { c.TenantId, c.MergedIntoId });

        // Name prefix search (the operator types the first letters of a name).
        // SearchKey is the one the search endpoint actually hits (US-M01-BE-12):
        // normalised once at write time so a LIKE 'prefix%' can use the index.
        builder.HasIndex(c => new { c.TenantId, c.SearchKey });
        builder.HasIndex(c => new { c.TenantId, c.LastName });
        builder.HasIndex(c => new { c.TenantId, c.FirstName });
        builder.HasIndex(c => new { c.TenantId, c.LegalName });
        builder.HasIndex(c => new { c.TenantId, c.DisplayName });

        // No physical foreign key on AgencyId / AdvisorUserId / SourceLeadId:
        // those rows live in the administration and leads schemas. Cross-module
        // references stay opaque so neither module can block the other's schema.
        builder.Ignore(c => c.IsReadOnly);
        builder.Ignore(c => c.DomainEvents);
    }
}
