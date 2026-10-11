using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Administration.Domain;

namespace Sankore.Modules.Administration.Infrastructure.Configurations;

internal sealed class DistributionCredentialConfiguration
    : IEntityTypeConfiguration<DistributionCredential>
{
    public void Configure(EntityTypeBuilder<DistributionCredential> b)
    {
        b.ToTable("distribution_credentials");
        b.HasKey(c => c.Id);

        b.Property(c => c.TenantId).IsRequired();
        b.Property(c => c.UserId).IsRequired();

        // Enums as strings, the repo convention: a reordered enum must not silently re-label
        // every stored row.
        b.Property(c => c.Category).HasConversion<string>().HasMaxLength(30).IsRequired();

        // Uncapped: a `v1:nonce:tag:ciphertext` payload grows with the plaintext and a cap is a
        // write that fails on a long card number rather than a validation that explains itself.
        b.Property(c => c.EncryptedCardNumber).IsRequired();

        // Lower-case hex of an HMAC-SHA256, so exactly 64 characters.
        b.Property(c => c.CardNumberBlindIndex).HasMaxLength(64).IsRequired();

        b.Property(c => c.IssuingAuthority).HasMaxLength(200).IsRequired();
        b.Property(c => c.IssuedOn).IsRequired();
        b.Property(c => c.ExpiresOn).IsRequired();

        b.Property(c => c.IndemnityInsurer).HasMaxLength(200);
        b.Property(c => c.IndemnityPolicyReference).HasMaxLength(100);

        b.Property(c => c.ClosedReason).HasMaxLength(500);

        b.Property(c => c.Version)
            .HasColumnName("xmin").HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();

        // Computed, never stored: both read the row they are called on.
        b.Ignore(c => c.IsCurrent);
        b.Ignore(c => c.HasIndemnityCover);

        // ── Indexes ─────────────────────────────────────────────────────────

        // The read every guard makes: the holder's current credential. Filtered, so the index
        // covers exactly the rows a dispatch or a subscription consults.
        b.HasIndex(c => new { c.TenantId, c.UserId })
            .HasFilter("valid_to IS NULL")
            .HasDatabaseName("ix_distribution_credentials_current");

        // At most ONE current credential per holder, enforced here and not only in the handler:
        // two current rows would have every guard read whichever came back first, which is the
        // kind of ambiguity a controller's question cannot survive.
        b.HasIndex(c => new { c.TenantId, c.UserId }, "ux_distribution_credentials_one_current")
            .IsUnique()
            .HasFilter("valid_to IS NULL")
            .HasDatabaseName("ux_distribution_credentials_one_current");

        // One card number may not be recorded twice as current in a tenant: a number belongs to
        // one person, and two holders sharing one is either a typo or a licence being passed
        // around. Over the blind index, because the number itself is ciphertext and
        // non-deterministic.
        b.HasIndex(c => new { c.TenantId, c.CardNumberBlindIndex })
            .IsUnique()
            .HasFilter("valid_to IS NULL")
            .HasDatabaseName("ux_distribution_credentials_card_number");

        // The expiry report, which sweeps a tenant by date.
        b.HasIndex(c => new { c.TenantId, c.ExpiresOn })
            .HasDatabaseName("ix_distribution_credentials_expiry");
    }
}
