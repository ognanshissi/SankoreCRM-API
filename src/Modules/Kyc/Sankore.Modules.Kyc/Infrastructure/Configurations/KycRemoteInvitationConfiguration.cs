namespace Sankore.Modules.Kyc.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Kyc.Domain;

internal sealed class KycRemoteInvitationConfiguration : IEntityTypeConfiguration<KycRemoteInvitation>
{
    /// <summary>
    /// The SQL half of <see cref="KycRemoteInvitation.ActiveStatuses"/>. Built from the enum rather
    /// than typed out, so the index filter and the aggregate's notion of "still usable" cannot
    /// drift — the two disagreeing would either allow two live links for one file or refuse a
    /// legitimate re-issue, and neither failure shows up until a customer is stuck.
    /// </summary>
    private static string ActiveStatusFilter()
        => "status IN ("
           + string.Join(", ", KycRemoteInvitation.ActiveStatuses.Select(s => $"'{s}'"))
           + ")";

    public void Configure(EntityTypeBuilder<KycRemoteInvitation> b)
    {
        b.ToTable("kyc_remote_invitations");
        b.HasKey(i => i.Id);

        // Stored as text like every other enum here: a value readable in psql is worth more during
        // a compliance audit than a few bytes per row.
        b.Property(i => i.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        b.Property(i => i.Purpose).HasConversion<string>().HasMaxLength(16).IsRequired();

        // bytea, fixed at the digest width. Not a hex string: a 64-character column invites
        // somebody to compare it with LIKE, and the only comparison that is ever correct here is
        // equality on the full digest.
        // Nullable: a Scheduled link has no token yet — it is minted by the send job, which is what
        // keeps the clear value out of the Hangfire payload.
        b.Property(i => i.TokenHash)
            .HasColumnType("bytea");

        b.Property(i => i.QualityRetriesJson)
            .HasColumnType("jsonb")
            .HasColumnName("quality_retries")
            // Same trap as M12's typed jsonb columns: EF generates `DEFAULT ''` for a non-nullable
            // string property and PostgreSQL rejects `''` on jsonb outright.
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        b.Property(i => i.ConsentTextVersion).HasMaxLength(32);

        // Same width as the other operator-motive columns of this module (KycApprovalStep.Comment,
        // KycDocument.RefusalReason, KycFile.ManualValidationReason).
        b.Property(i => i.RevokeReason).HasMaxLength(2000);

        b.Property(i => i.Version).IsRowVersion();

        // THE lookup of the public endpoints: hash the token, probe here. Unique across the whole
        // table and not per tenant, deliberately — a 32-byte random value colliding is not a
        // scenario, and a tenant-scoped index would let the same token resolve to two files, which
        // is exactly the cross-tenant confusion the FQDN check exists to prevent.
        b.HasIndex(i => i.TokenHash)
            .IsUnique()
            // PARTIAL: every Scheduled row carries a null hash, and PostgreSQL treats nulls as
            // distinct in a unique index — so this filter is not what makes it correct, it is what
            // keeps the index to the links that actually have a token. Spelled out because a reader
            // would otherwise wonder how several null hashes coexist.
            .HasFilter("token_hash IS NOT NULL")
            .HasDatabaseName("ux_kyc_remote_invitations_token");

        // ONE active link per file. Filtered, so spent links stay as history: the dates on them
        // are what tell an agent whether the customer ever opened the last three.
        b.HasIndex(i => new { i.TenantId, i.KycFileId })
            .IsUnique()
            .HasFilter(ActiveStatusFilter())
            .HasDatabaseName("ux_kyc_remote_invitations_active_per_file");

        // The history panel of one file, newest first.
        b.HasIndex(i => new { i.TenantId, i.KycFileId, i.CreatedAt })
            .HasDatabaseName("ix_kyc_remote_invitations_file_created");

        // The nightly expiry sweep: per tenant, by window. Narrowed to the statuses that can
        // actually expire, so the index stays the size of the links in flight rather than the size
        // of every link ever issued.
        b.HasIndex(i => new { i.TenantId, i.ExpiresAt })
            .HasFilter(ActiveStatusFilter())
            .HasDatabaseName("ix_kyc_remote_invitations_expiry");

        b.Ignore(i => i.DomainEvents);
        b.Ignore(i => i.IsActive);
    }
}
