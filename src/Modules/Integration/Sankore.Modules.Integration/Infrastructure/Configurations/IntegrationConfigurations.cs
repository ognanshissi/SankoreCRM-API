namespace Sankore.Modules.Integration.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Integration.Domain;

internal sealed class IntegrationConnectionConfiguration : IEntityTypeConfiguration<IntegrationConnection>
{
    public void Configure(EntityTypeBuilder<IntegrationConnection> b)
    {
        b.ToTable("integration_connection");
        b.HasKey(c => c.Id);

        b.Property(c => c.Family).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(c => c.Mode).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.Name).HasMaxLength(120).IsRequired();
        b.Property(c => c.LastHealthDetail).HasMaxLength(1000);

        b.Property(c => c.Settings)
            .HasColumnName("settings")
            .HasColumnType("jsonb")
            .HasConversion(new ConnectionSettingsConverter())
            .Metadata.SetValueComparer(new ConnectionSettingsComparer());

        b.Property(c => c.Version).IsRowVersion();

        // The asymmetry of ASS-01, as a database guarantee: ONE active core-banking connection
        // per tenant, any number of active insurance ones. In the index and not in the handler
        // because two concurrent activations would both pass an in-memory check.
        b.HasIndex(c => c.TenantId)
            .IsUnique()
            .HasFilter("is_active AND family = 'CoreBanking'")
            .HasDatabaseName("ux_integration_connection_active_core_banking");

        b.HasIndex(c => new { c.TenantId, c.Family, c.IsActive })
            .HasDatabaseName("ix_integration_connection_tenant_family_active");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class IntegrationCommandConfiguration : IEntityTypeConfiguration<IntegrationCommand>
{
    public void Configure(EntityTypeBuilder<IntegrationCommand> b)
    {
        b.ToTable("integration_command");
        b.HasKey(c => c.Id);

        b.Property(c => c.CommandType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(c => c.EntityType).HasMaxLength(60).IsRequired();
        b.Property(c => c.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.LastErrorFamily).HasConversion<string>().HasMaxLength(20);
        b.Property(c => c.LastErrorMessage).HasMaxLength(1000);
        b.Property(c => c.ExternalResponseRef).HasMaxLength(200);
        b.Property(c => c.PayloadFieldNames).HasMaxLength(2000);

        // The encrypted payload. No length cap: "v1:nonce:tag:ciphertext" grows with the payload
        // and a bounded column would truncate a customer with a long address into garbage.
        b.Property(c => c.PayloadEncrypted).HasColumnType("text");

        b.Property(c => c.Version).IsRowVersion();

        // What makes a replay safe (INT-05).
        b.HasIndex(c => new { c.TenantId, c.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_integration_command_tenant_idempotency");

        // The dispatcher's own query: what is due, across tenants. Deliberately NOT prefixed by
        // tenant_id — the orchestrator asks "which tenants owe work" before it knows the tenant.
        b.HasIndex(c => new { c.Status, c.NextAttemptAt })
            .HasDatabaseName("ix_integration_command_status_next_attempt");

        // Per-customer ordering (INT-06): the commands of one client, oldest first.
        b.HasIndex(c => new { c.TenantId, c.EntityType, c.CrmId, c.CreatedAt })
            .HasDatabaseName("ix_integration_command_tenant_entity_created");

        b.HasIndex(c => c.BatchFileId)
            .HasFilter("batch_file_id IS NOT NULL")
            .HasDatabaseName("ix_integration_command_batch_file");

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class IntegrationReferenceConfiguration : IEntityTypeConfiguration<IntegrationReference>
{
    public void Configure(EntityTypeBuilder<IntegrationReference> b)
    {
        b.ToTable("integration_reference");
        b.HasKey(r => r.Id);

        b.Property(r => r.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(r => r.EntityType).HasMaxLength(60).IsRequired();
        b.Property(r => r.ExternalId).HasMaxLength(200).IsRequired();

        // Unique in BOTH directions, per connection (INT-07). One without the other leaves the
        // half nobody checked open to duplicates.
        b.HasIndex(r => new { r.TenantId, r.ConnectionId, r.EntityType, r.CrmId })
            .IsUnique()
            .HasDatabaseName("ux_integration_reference_crm");

        b.HasIndex(r => new { r.TenantId, r.ConnectionId, r.EntityType, r.ExternalId })
            .IsUnique()
            .HasDatabaseName("ux_integration_reference_external");

        b.Ignore(r => r.DomainEvents);
    }
}

internal sealed class IntegrationBatchFileConfiguration : IEntityTypeConfiguration<IntegrationBatchFile>
{
    public void Configure(EntityTypeBuilder<IntegrationBatchFile> b)
    {
        b.ToTable("integration_batch_file");
        b.HasKey(f => f.Id);

        b.Property(f => f.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        b.Property(f => f.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(f => f.FileName).HasMaxLength(260).IsRequired();
        b.Property(f => f.ChecksumSha256).HasMaxLength(64).IsRequired();
        b.Property(f => f.StorageRef).HasMaxLength(500);
        b.Property(f => f.FailureDetail).HasMaxLength(1000);
        b.Property(f => f.Version).IsRowVersion();

        b.HasIndex(f => new { f.TenantId, f.ConnectionId, f.Direction, f.SequenceNo })
            .IsUnique()
            .HasDatabaseName("ux_integration_batch_file_sequence");

        b.HasIndex(f => new { f.TenantId, f.Status })
            .HasDatabaseName("ix_integration_batch_file_tenant_status");

        b.Ignore(f => f.DomainEvents);
    }
}

internal sealed class IntegrationSyncCursorConfiguration : IEntityTypeConfiguration<IntegrationSyncCursor>
{
    public void Configure(EntityTypeBuilder<IntegrationSyncCursor> b)
    {
        b.ToTable("integration_sync_cursor");

        // Composite key, as specified: exactly one cursor per (tenant, connection, stream).
        b.HasKey(c => new { c.TenantId, c.ConnectionId, c.Stream });

        b.Property(c => c.Stream).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(c => c.Cursor).HasMaxLength(500);
        b.Property(c => c.LastError).HasMaxLength(1000);
        b.Property(c => c.Version).IsRowVersion();

        b.Ignore(c => c.DomainEvents);
    }
}

internal sealed class IntegrationMappingConfiguration : IEntityTypeConfiguration<IntegrationMapping>
{
    public void Configure(EntityTypeBuilder<IntegrationMapping> b)
    {
        b.ToTable("integration_mapping");
        b.HasKey(m => m.Id);

        b.Property(m => m.Domain).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(m => m.CrmCode).HasMaxLength(100).IsRequired();
        b.Property(m => m.ExternalCode).HasMaxLength(100).IsRequired();
        b.Property(m => m.Label).HasMaxLength(200);
        b.Property(m => m.Version).IsRowVersion();

        b.HasIndex(m => new { m.TenantId, m.ConnectionId, m.Domain, m.CrmCode })
            .IsUnique()
            .HasDatabaseName("ux_integration_mapping_crm_code");

        // The reverse lookup the snapshot needs to translate CBS codes back to CRM ones (INT-21).
        // Not unique: two CRM codes may legitimately fold onto one external code.
        b.HasIndex(m => new { m.TenantId, m.ConnectionId, m.Domain, m.ExternalCode })
            .HasDatabaseName("ix_integration_mapping_external_code");

        b.Ignore(m => m.DomainEvents);
    }
}

internal sealed class IntegrationReconciliationRunConfiguration
    : IEntityTypeConfiguration<IntegrationReconciliationRun>
{
    public void Configure(EntityTypeBuilder<IntegrationReconciliationRun> b)
    {
        b.ToTable("integration_reconciliation_run");
        b.HasKey(r => r.Id);

        b.Property(r => r.FailureDetail).HasMaxLength(1000);

        // A comma-separated list of GapType NAMES, so the longest possible value is every name
        // of the enum joined — well under 120 even if the enum doubles. Deliberately not a
        // jsonb array: nothing queries inside it, and a report prints it as it stands.
        b.Property(r => r.UndetectableGapTypes).HasMaxLength(120);

        b.HasIndex(r => new { r.TenantId, r.StartedAt })
            .HasDatabaseName("ix_integration_reconciliation_run_tenant_started");

        b.Ignore(r => r.DomainEvents);
    }
}

internal sealed class IntegrationReconciliationGapConfiguration
    : IEntityTypeConfiguration<IntegrationReconciliationGap>
{
    public void Configure(EntityTypeBuilder<IntegrationReconciliationGap> b)
    {
        b.ToTable("integration_reconciliation_gap");
        b.HasKey(g => g.Id);

        b.Property(g => g.GapType).HasConversion<string>().HasMaxLength(30).IsRequired();
        b.Property(g => g.Resolution).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(g => g.ExternalId).HasMaxLength(200);
        b.Property(g => g.ResolutionNote).HasMaxLength(1000);
        b.Property(g => g.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
        b.Property(g => g.Version).IsRowVersion();

        b.HasIndex(g => new { g.TenantId, g.Resolution })
            .HasDatabaseName("ix_integration_reconciliation_gap_tenant_resolution");

        // What makes a still-true gap not become a second row tomorrow (INT-34). Filtered on the
        // open ones: the same gap may legitimately reappear after being resolved months later.
        //
        // NULLS NOT DISTINCT is load-bearing, not a refinement. Two of the four gap types leave one
        // of these columns null by contract — MissingInExternal has no external id, MissingInCrm no
        // CRM id — and PostgreSQL's default treats NULLs as DISTINCT, so for exactly those two the
        // index constrained nothing: two identical open rows were accepted. Verified on PostgreSQL
        // 18 before changing it, and the control case (both columns set) was correctly refused,
        // which is what made the hole invisible. The reconciliation's own dedup reads the open set
        // first and does not depend on this, but two overlapping runs for one connection — Hangfire
        // retries at least once — would both insert, and then criterion 3 is enforced by nothing.
        b.HasIndex(g => new { g.TenantId, g.ConnectionId, g.GapType, g.CrmId, g.ExternalId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("resolution = 'Open'")
            .HasDatabaseName("ux_integration_reconciliation_gap_open");

        b.Ignore(g => g.DomainEvents);
    }
}

internal sealed class IntegrationCallLogConfiguration : IEntityTypeConfiguration<IntegrationCallLog>
{
    public void Configure(EntityTypeBuilder<IntegrationCallLog> b)
    {
        b.ToTable("integration_call_log");

        // PostgreSQL requires the partition key inside every unique constraint, so the key is
        // (id, at) and not (id). The migration turns this table into a partitioned parent.
        b.HasKey(l => new { l.Id, l.At });

        b.Property(l => l.Operation).HasMaxLength(60).IsRequired();
        b.Property(l => l.Endpoint).HasMaxLength(500);
        b.Property(l => l.ErrorFamily).HasConversion<string>().HasMaxLength(20);
        b.Property(l => l.ErrorCode).HasMaxLength(80);
        b.Property(l => l.CorrelationId).HasMaxLength(100);

        b.HasIndex(l => new { l.TenantId, l.At })
            .HasDatabaseName("ix_integration_call_log_tenant_at");

        b.HasIndex(l => l.CommandId)
            .HasFilter("command_id IS NOT NULL")
            .HasDatabaseName("ix_integration_call_log_command");

        b.Ignore(l => l.DomainEvents);
    }
}

internal sealed class CbsSnapshotConfiguration : IEntityTypeConfiguration<CbsSnapshot>
{
    public void Configure(EntityTypeBuilder<CbsSnapshot> b)
    {
        b.ToTable("cbs_customer_snapshot");

        // Composite key, as specified: one snapshot per customer, guaranteed by the key.
        b.HasKey(s => new { s.TenantId, s.CrmCustomerId });

        b.Property(s => s.AccountsJson).HasColumnName("accounts").HasColumnType("jsonb").IsRequired();
        b.Property(s => s.LoansJson).HasColumnName("loans").HasColumnType("jsonb").IsRequired();
        b.Property(s => s.TotalBalance).HasPrecision(18, 2);
        b.Property(s => s.MonthlyFlow).HasPrecision(18, 2);
        b.Property(s => s.KycLevelInCbs).HasConversion<string>().HasMaxLength(20);
        b.Property(s => s.Version).IsRowVersion();

        b.HasIndex(s => new { s.TenantId, s.SnapshotAt })
            .HasDatabaseName("ix_cbs_customer_snapshot_tenant_snapshot_at");

        b.Ignore(s => s.DomainEvents);
    }
}

internal sealed class KycLimitAlertConfiguration : IEntityTypeConfiguration<KycLimitAlert>
{
    public void Configure(EntityTypeBuilder<KycLimitAlert> b)
    {
        b.ToTable("integration_kyc_limit_alert");
        b.HasKey(a => a.Id);

        b.Property(a => a.LimitKind).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(a => a.Period).HasMaxLength(7).IsRequired();
        b.Property(a => a.Observed).HasPrecision(18, 2);
        b.Property(a => a.Ceiling).HasPrecision(18, 2);

        // THE point of the table (INT-22): one alert per customer, ceiling, severity and month.
        // A unique index and not a read-then-write, because the watch job runs per tenant and a
        // retry after a partial failure would otherwise raise the same alert twice — which for an
        // operator means a second upgrade task for a customer who already has one.
        b.HasIndex(a => new { a.TenantId, a.CrmCustomerId, a.LimitKind, a.Severity, a.Period })
            .IsUnique()
            .HasDatabaseName("ux_integration_kyc_limit_alert_period");

        b.HasIndex(a => new { a.TenantId, a.RaisedAt })
            .HasDatabaseName("ix_integration_kyc_limit_alert_tenant_raised");

        b.Ignore(a => a.DomainEvents);
    }
}

internal sealed class IntegrationRelayAgentConfiguration : IEntityTypeConfiguration<IntegrationRelayAgent>
{
    public void Configure(EntityTypeBuilder<IntegrationRelayAgent> b)
    {
        b.ToTable("integration_relay_agent");
        b.HasKey(a => a.Id);

        b.Property(a => a.Name).HasMaxLength(120).IsRequired();
        b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Both are SHA-256 hex: 64 characters, bounded so a malformed value cannot be stored.
        b.Property(a => a.EnrolmentTokenHash).HasMaxLength(64);
        b.Property(a => a.CertificateThumbprint).HasMaxLength(64);

        b.Property(a => a.ReportedVersion).HasMaxLength(50);
        b.Property(a => a.ReportedStatusDetail).HasMaxLength(500);

        // Read whole, for one agent, to render one panel — see the entity's remarks for why this
        // is jsonb and not a child table.
        b.Property(a => a.ReportedTargetsJson).HasColumnName("reported_targets").HasColumnType("jsonb");
        b.Property(a => a.Version).IsRowVersion();

        // The enrolment lookup is BY the token hash, across tenants: the agent presenting it has
        // no tenant context yet — that is what the exchange establishes. Unique and filtered, so
        // a used or revoked agent (hash cleared to null) leaves the slot free.
        b.HasIndex(a => a.EnrolmentTokenHash)
            .IsUnique()
            .HasFilter("enrolment_token_hash IS NOT NULL")
            .HasDatabaseName("ux_integration_relay_agent_enrolment_token");

        // Same reasoning for the session: an agent connects with a certificate and no tenant
        // header, so the thumbprint is what resolves it.
        b.HasIndex(a => a.CertificateThumbprint)
            .IsUnique()
            .HasFilter("certificate_thumbprint IS NOT NULL")
            .HasDatabaseName("ux_integration_relay_agent_certificate");

        b.HasIndex(a => new { a.TenantId, a.Status })
            .HasDatabaseName("ix_integration_relay_agent_tenant_status");

        b.Ignore(a => a.DomainEvents);
    }
}

internal sealed class IntegrationInboxMessageConfiguration
    : IEntityTypeConfiguration<IntegrationInboxMessage>
{
    public void Configure(EntityTypeBuilder<IntegrationInboxMessage> b)
    {
        b.ToTable("inbox_messages");
        b.HasKey(m => m.Id);

        b.Property(m => m.EventType).HasMaxLength(200).IsRequired();

        b.HasIndex(m => new { m.TenantId, m.ReceivedAt })
            .HasDatabaseName("ix_integration_inbox_tenant_received");
    }
}
