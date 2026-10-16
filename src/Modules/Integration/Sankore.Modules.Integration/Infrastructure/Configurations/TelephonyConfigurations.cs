namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The telephony tables (TEL-B-03). Prefix <c>tel_</c>, as <c>cbs_</c> is core banking and
/// <c>ins_</c> insurance — the convention test reads the prefix to decide which family a table
/// belongs to.
///
/// <para>
/// None of them carries a foreign key into <c>leads</c>, <c>customers</c> or <c>identity</c>:
/// <c>entity_type</c>/<c>entity_id</c> and <c>crm_user_id</c> are logical references, so another
/// module stays free to delete what it owns.
/// </para>
/// </summary>
internal sealed class TelUserLinkConfiguration : IEntityTypeConfiguration<TelUserLink>
{
    public void Configure(EntityTypeBuilder<TelUserLink> b)
    {
        b.ToTable("tel_user_link");
        b.HasKey(l => l.Id);

        b.Property(l => l.ExternalUserId).HasMaxLength(100).IsRequired();
        b.Property(l => l.ExternalEmail).HasMaxLength(320);
        b.Property(l => l.DefaultCallerNumber).HasMaxLength(20);
        b.Property(l => l.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(l => l.StatusReason).HasMaxLength(500);
        b.Property(l => l.Version).IsRowVersion();

        // One to one, in BOTH directions. Two CRM users on one provider account would attribute
        // each other's calls; one user on two accounts makes "which handset" unanswerable.
        b.HasIndex(l => new { l.TenantId, l.ConnectionId, l.CrmUserId })
            .IsUnique()
            .HasDatabaseName("ux_tel_user_link_crm_user");

        b.HasIndex(l => new { l.TenantId, l.ConnectionId, l.ExternalUserId })
            .IsUnique()
            .HasDatabaseName("ux_tel_user_link_external_user");

        b.Ignore(l => l.DomainEvents);
    }
}

internal sealed class TelCallConfiguration : IEntityTypeConfiguration<TelCall>
{
    public void Configure(EntityTypeBuilder<TelCall> b)
    {
        b.ToTable("tel_call", t => t.HasCheckConstraint(
            "ck_tel_call_entity", "(entity_type IS NULL) = (entity_id IS NULL)"));

        b.HasKey(c => c.Id);

        b.Property(c => c.ExternalCallId).HasMaxLength(100).IsRequired();
        b.Property(c => c.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(c => c.MatchStatus).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(c => c.MatchMethod).HasConversion<string>().HasMaxLength(15);
        b.Property(c => c.RecordingStatus).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(c => c.ExternalUserId).HasMaxLength(100);
        b.Property(c => c.EntityType).HasMaxLength(30);
        b.Property(c => c.OutcomeCode).HasMaxLength(30);

        // xmin: a webhook and the catch-up sync can reach the same row in the same second.
        b.Property(c => c.Version).IsRowVersion();

        // The row a redelivered webhook and the catch-up sync must converge on.
        b.HasIndex(c => new { c.TenantId, c.ConnectionId, c.ExternalCallId })
            .IsUnique()
            .HasDatabaseName("ux_tel_call_external");

        // "The calls of this record, newest first" — the lead and client screens.
        b.HasIndex(c => new { c.TenantId, c.EntityType, c.EntityId, c.StartedAt })
            .HasDatabaseName("ix_tel_call_entity");

        // "My calls" and the per-agent indicators.
        b.HasIndex(c => new { c.TenantId, c.CrmUserId, c.StartedAt })
            .HasDatabaseName("ix_tel_call_user");

        // The recomputation of one day (TEL-B-19): every call of a tenant whose StartedAt falls in
        // a 24-hour range, read in one pass. Tenant and instant only — the grouping keys are not
        // in the index because the whole day is scanned anyway, and putting CrmUserId before
        // StartedAt would make the range a scan per agent instead of one.
        b.HasIndex(c => new { c.TenantId, c.StartedAt })
            .HasDatabaseName("ix_tel_call_started");

        // The reverse caller-id lookup. On the blind index, the only thing a query may match on.
        b.HasIndex(c => new { c.TenantId, c.FromNumberBi })
            .HasDatabaseName("ix_tel_call_from_number");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class TelClickToCallConfiguration : IEntityTypeConfiguration<TelClickToCall>
{
    public void Configure(EntityTypeBuilder<TelClickToCall> b)
    {
        b.ToTable("tel_click_to_call");
        b.HasKey(r => r.Id);

        b.Property(r => r.EntityType).HasMaxLength(30).IsRequired();
        b.Property(r => r.PhoneSlot).HasMaxLength(20).IsRequired();
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(r => r.ExternalRef).HasMaxLength(100);
        b.Property(r => r.ErrorCode).HasMaxLength(60);
        b.Property(r => r.Version).IsRowVersion();

        // The correlation read: this user's requests still inside their window, newest first.
        b.HasIndex(r => new { r.TenantId, r.CrmUserId, r.RequestedAt })
            .HasDatabaseName("ix_tel_click_to_call_user");

        b.Ignore(r => r.DomainEvents);
    }
}

internal sealed class TelWebhookEventConfiguration : IEntityTypeConfiguration<TelWebhookEvent>
{
    public void Configure(EntityTypeBuilder<TelWebhookEvent> b)
    {
        b.ToTable("tel_webhook_event");

        // (id, received_at) and not (id): PostgreSQL requires the partition key inside every
        // unique constraint of a partitioned table. Same shape as integration_call_log.
        b.HasKey(e => new { e.Id, e.ReceivedAt });

        b.Property(e => e.DedupKey).HasMaxLength(200).IsRequired();
        b.Property(e => e.EventType).HasMaxLength(60).IsRequired();
        b.Property(e => e.Outcome).HasMaxLength(200);

        // THE idempotency of the whole webhook path: the insert is the check. The partition key
        // is inside the index because a unique index on a partitioned table must contain it —
        // which bounds deduplication to a month, and a provider redelivering across a month
        // boundary is not a case anybody has.
        b.HasIndex(e => new { e.TenantId, e.ConnectionId, e.DedupKey, e.ReceivedAt })
            .IsUnique()
            .HasDatabaseName("ux_tel_webhook_event_dedup");

        b.Ignore(e => e.DomainEvents);
    }
}

internal sealed class TelRecordingConfiguration : IEntityTypeConfiguration<TelRecording>
{
    public void Configure(EntityTypeBuilder<TelRecording> b)
    {
        b.ToTable("tel_recording");
        b.HasKey(r => r.Id);

        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(15).IsRequired();
        b.Property(r => r.StorageRef).HasMaxLength(120);
        b.Property(r => r.LegalBasis).HasMaxLength(200).IsRequired();
        b.Property(r => r.LegalHoldReason).HasMaxLength(500);
        b.Property(r => r.Version).IsRowVersion();

        // One recording per call.
        b.HasIndex(r => new { r.TenantId, r.CallId })
            .IsUnique()
            .HasDatabaseName("ux_tel_recording_call");

        // The purge sweep: what is due, per tenant. Narrowed to the rows that can actually be
        // purged, so the index stays the size of what is held rather than of what ever was.
        b.HasIndex(r => new { r.TenantId, r.RetentionUntil })
            .HasFilter("status = 'Available' AND legal_hold = false")
            .HasDatabaseName("ix_tel_recording_due");

        b.Ignore(r => r.DomainEvents);
    }
}

internal sealed class TelCallOutcomeConfiguration : IEntityTypeConfiguration<TelCallOutcome>
{
    public void Configure(EntityTypeBuilder<TelCallOutcome> b)
    {
        b.ToTable("tel_call_outcome");

        // The code IS the key, per tenant: a call stores the code and nothing else, so a
        // surrogate id would let two rows claim the same meaning.
        b.HasKey(o => new { o.TenantId, o.Code });

        b.Property(o => o.Code).HasMaxLength(30);
        b.Property(o => o.Label).HasMaxLength(100).IsRequired();

        b.Ignore(o => o.DomainEvents);
    }
}

internal sealed class TelRecordingPolicyConfiguration : IEntityTypeConfiguration<TelRecordingPolicy>
{
    public void Configure(EntityTypeBuilder<TelRecordingPolicy> b)
    {
        b.ToTable("tel_recording_policy");

        // The TENANT is the key: one policy per institution, and a surrogate id would let two rows
        // claim to be it. Same shape as tel_call_outcome's composite key, one segment shorter.
        b.HasKey(p => p.TenantId);

        b.Property(p => p.Mode).HasConversion<string>().HasMaxLength(15).IsRequired();

        // Free text in the compliance officer's own words, and long: a lawful basis cites a text
        // and an institution's own reading of it. 1000 is what the KYC consent wording uses.
        b.Property(p => p.LegalBasis).HasMaxLength(1000);
        b.Property(p => p.Purpose).HasMaxLength(1000);

        b.Property(p => p.Version).IsRowVersion();

        b.Ignore(p => p.DomainEvents);
    }
}

/// <summary>
/// Who opened a recording, and why (TEL-B-17).
///
/// <para>
/// Its own table rather than a column on <c>tel_recording</c>, for the reason M01's
/// <c>SensitiveDataAccessLog</c> is its own: a recording is listened to many times by many people,
/// and a column would hold the last one. What a regulator asks is who has heard this, and only a
/// row per access can answer it.
/// </para>
/// </summary>
internal sealed class TelRecordingAccessLogConfiguration
    : IEntityTypeConfiguration<TelRecordingAccessLog>
{
    public void Configure(EntityTypeBuilder<TelRecordingAccessLog> b)
    {
        b.ToTable("tel_recording_access_log");
        b.HasKey(l => l.Id);

        b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(l => l.Reason).HasMaxLength(500);
        b.Property(l => l.CorrelationId).HasMaxLength(120);

        // "Who has heard this recording", which is the question the table exists for.
        b.HasIndex(l => new { l.TenantId, l.RecordingId, l.AccessedAt })
            .HasDatabaseName("ix_tel_recording_access_recording");

        // And its mirror: "what has this person listened to", which is how a disproportionate
        // listener is found. Without it that question is a scan of every access ever recorded.
        b.HasIndex(l => new { l.TenantId, l.ActorUserId, l.AccessedAt })
            .HasDatabaseName("ix_tel_recording_access_actor");

        b.Ignore(l => l.DomainEvents);
    }
}


/// <summary>
/// The indicators' read model (TEL-B-19). <b>Nothing points at it and it points at nothing</b> —
/// no foreign key, no row version, no audit: it is derived, so the only honest reaction to a
/// disagreement with <c>tel_call</c> is to derive it again.
/// </summary>
internal sealed class TelCallDailyStatConfiguration : IEntityTypeConfiguration<TelCallDailyStat>
{
    public void Configure(EntityTypeBuilder<TelCallDailyStat> b)
    {
        b.ToTable("tel_call_daily_stat");
        b.HasKey(s => s.Id);

        // date, not timestamptz: the column IS a day, and storing it as an instant would invite a
        // reader to apply a time zone to something that no longer has one.
        b.Property(s => s.StatDate).HasColumnType("date").IsRequired();

        // One row per bucket, enforced and not merely intended. Both grouping columns are
        // non-nullable with Guid.Empty for "none" precisely so this index constrains every row —
        // a nullable column here would let PostgreSQL treat each unattributed bucket as distinct
        // and the recomputation would stack duplicates one run at a time.
        b.HasIndex(s => new { s.TenantId, s.StatDate, s.CrmUserId, s.AgencyId })
            .IsUnique()
            .HasDatabaseName("ux_tel_call_daily_stat_bucket");

        // "This agency, over this period" — the per-agency view, and the sweep that deletes a
        // range before recomputing it.
        b.HasIndex(s => new { s.TenantId, s.AgencyId, s.StatDate })
            .HasDatabaseName("ix_tel_call_daily_stat_agency");

        b.Ignore(s => s.DomainEvents);
    }
}
