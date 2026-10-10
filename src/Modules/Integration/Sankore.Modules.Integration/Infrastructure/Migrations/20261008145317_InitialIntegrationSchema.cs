using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegrationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integration");

            migrationBuilder.CreateTable(
                name: "cbs_customer_snapshot",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accounts = table.Column<string>(type: "jsonb", nullable: false),
                    loans = table.Column<string>(type: "jsonb", nullable: false),
                    total_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    monthly_flow = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    kyc_level_in_cbs = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    snapshot_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cbs_customer_snapshot", x => new { x.tenant_id, x.crm_customer_id });
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_batch_file",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sequence_no = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ack_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_batch_file", x => x.id);
                });

            // ── integration_call_log: PARTITIONED, written by hand ──────────────────────────
            //
            // Not migrationBuilder.CreateTable: EF cannot express PARTITION BY, and this is the
            // highest-volume table of the module by an order of magnitude — one row per outbound
            // call, every call, kept for the BCEAO and CIMA controls. Monthly range partitions are
            // what let an expired month be DETACHed and DROPped in constant time instead of a
            // DELETE that rewrites the table and leaves it to be vacuumed.
            //
            // The primary key is (id, at) and not (id) because PostgreSQL requires the partition
            // key inside every unique constraint on a partitioned table. The entity's key matches,
            // so EF and the database agree.
            //
            // The indexes below are created by the scaffolded CreateIndex calls and become
            // PARTITIONED indexes, inherited by every partition present and future — which is why
            // they are not repeated here.
            migrationBuilder.Sql("""
                CREATE TABLE integration.integration_call_log (
                    id              uuid                     NOT NULL,
                    at              timestamp with time zone NOT NULL,
                    connection_id   uuid                     NOT NULL,
                    command_id      uuid                     NULL,
                    operation       character varying(60)    NOT NULL,
                    endpoint        character varying(500)   NULL,
                    http_status     integer                  NULL,
                    duration_ms     bigint                   NOT NULL,
                    error_family    character varying(20)    NULL,
                    error_code      character varying(80)    NULL,
                    correlation_id  character varying(100)   NULL,
                    tenant_id       uuid                     NOT NULL,
                    CONSTRAINT pk_integration_call_log PRIMARY KEY (id, at)
                ) PARTITION BY RANGE (at);
                """);

            // The current month plus three ahead. EnsureCallLogPartitionsJob keeps that window
            // moving; this seeds it so the first insert after a fresh deploy has somewhere to go.
            // An INSERT with no matching partition FAILS — it is not routed anywhere, and it is
            // not a warning — so the job is part of the feature, not housekeeping around it.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    start_month date := date_trunc('month', now())::date;
                    i int;
                    from_date date;
                    to_date date;
                    part_name text;
                BEGIN
                    FOR i IN 0..3 LOOP
                        from_date := (start_month + (i || ' month')::interval)::date;
                        to_date   := (start_month + ((i + 1) || ' month')::interval)::date;
                        part_name := format('integration_call_log_y%sm%s',
                                            to_char(from_date, 'YYYY'), to_char(from_date, 'MM'));

                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS integration.%I '
                            || 'PARTITION OF integration.integration_call_log '
                            || 'FOR VALUES FROM (%L) TO (%L)',
                            part_name, from_date, to_date);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.CreateTable(
                name: "integration_command",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    crm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload_encrypted = table.Column<string>(type: "text", nullable: true),
                    payload_field_names = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_response_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    batch_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_command", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_connection",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: true),
                    relay_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_health_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_health_status = table.Column<bool>(type: "boolean", nullable: true),
                    last_health_detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_connection", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_mapping",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    domain = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    crm_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    external_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_mapping", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_reconciliation_gap",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gap_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    crm_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_reconciliation_gap", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_reconciliation_run",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_count = table.Column<int>(type: "integer", nullable: false),
                    gap_count = table.Column<int>(type: "integer", nullable: false),
                    closed_count = table.Column<int>(type: "integer", nullable: false),
                    failure_detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_reconciliation_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_reference",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    crm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_reference", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_sync_cursor",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    cursor = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_sync_cursor", x => new { x.tenant_id, x.connection_id, x.stream });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cbs_customer_snapshot_tenant_snapshot_at",
                schema: "integration",
                table: "cbs_customer_snapshot",
                columns: new[] { "tenant_id", "snapshot_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_inbox_tenant_received",
                schema: "integration",
                table: "inbox_messages",
                columns: new[] { "tenant_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_batch_file_tenant_status",
                schema: "integration",
                table: "integration_batch_file",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_batch_file_sequence",
                schema: "integration",
                table: "integration_batch_file",
                columns: new[] { "tenant_id", "connection_id", "direction", "sequence_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_call_log_command",
                schema: "integration",
                table: "integration_call_log",
                column: "command_id",
                filter: "command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_call_log_tenant_at",
                schema: "integration",
                table: "integration_call_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_command_batch_file",
                schema: "integration",
                table: "integration_command",
                column: "batch_file_id",
                filter: "batch_file_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_integration_command_status_next_attempt",
                schema: "integration",
                table: "integration_command",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_command_tenant_entity_created",
                schema: "integration",
                table: "integration_command",
                columns: new[] { "tenant_id", "entity_type", "crm_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_command_tenant_idempotency",
                schema: "integration",
                table: "integration_command",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_connection_tenant_family_active",
                schema: "integration",
                table: "integration_connection",
                columns: new[] { "tenant_id", "family", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_connection_active_core_banking",
                schema: "integration",
                table: "integration_connection",
                column: "tenant_id",
                unique: true,
                filter: "is_active AND family = 'CoreBanking'");

            migrationBuilder.CreateIndex(
                name: "ix_integration_mapping_external_code",
                schema: "integration",
                table: "integration_mapping",
                columns: new[] { "tenant_id", "connection_id", "domain", "external_code" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_mapping_crm_code",
                schema: "integration",
                table: "integration_mapping",
                columns: new[] { "tenant_id", "connection_id", "domain", "crm_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_reconciliation_gap_tenant_resolution",
                schema: "integration",
                table: "integration_reconciliation_gap",
                columns: new[] { "tenant_id", "resolution" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_reconciliation_gap_open",
                schema: "integration",
                table: "integration_reconciliation_gap",
                columns: new[] { "tenant_id", "connection_id", "gap_type", "crm_id", "external_id" },
                unique: true,
                filter: "resolution = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_integration_reconciliation_run_tenant_started",
                schema: "integration",
                table: "integration_reconciliation_run",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_reference_crm",
                schema: "integration",
                table: "integration_reference",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "crm_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_integration_reference_external",
                schema: "integration",
                table: "integration_reference",
                columns: new[] { "tenant_id", "connection_id", "entity_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "integration",
                table: "outbox_messages",
                columns: new[] { "processed_at", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cbs_customer_snapshot",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_batch_file",
                schema: "integration");

            // CASCADE: a partitioned parent cannot be dropped while its partitions exist, and the
            // scaffolded DropTable issues a plain DROP TABLE.
            migrationBuilder.Sql("DROP TABLE IF EXISTS integration.integration_call_log CASCADE;");

            migrationBuilder.DropTable(
                name: "integration_command",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_connection",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_mapping",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_reconciliation_gap",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_reconciliation_run",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_reference",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "integration_sync_cursor",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "integration");
        }
    }
}
