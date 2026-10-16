using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelephonyFamily : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_integration_connection_active_core_banking",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.CreateTable(
                name: "tel_call",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_call_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    from_number_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    from_number_bi = table.Column<byte[]>(type: "bytea", nullable: false),
                    to_number_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    to_number_bi = table.Column<byte[]>(type: "bytea", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    crm_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    match_status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    match_method = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    click_to_call_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ring_seconds = table.Column<int>(type: "integer", nullable: true),
                    talk_seconds = table.Column<int>(type: "integer", nullable: true),
                    outcome_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    note_enc = table.Column<byte[]>(type: "bytea", nullable: true),
                    wrapped_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    keep_recording = table.Column<bool>(type: "boolean", nullable: false),
                    recording_status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_call", x => x.id);
                    table.CheckConstraint("ck_tel_call_entity", "(entity_type IS NULL) = (entity_id IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "tel_call_outcome",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    counts_as_contact = table.Column<bool>(type: "boolean", nullable: false),
                    requires_callback_date = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_call_outcome", x => new { x.tenant_id, x.code });
                });

            migrationBuilder.CreateTable(
                name: "tel_click_to_call",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_slot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_number_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    target_number_bi = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    external_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_click_to_call", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tel_recording",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    legal_basis = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    retention_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legal_hold = table.Column<bool>(type: "boolean", nullable: false),
                    legal_hold_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_recording", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tel_user_link",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    external_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    default_caller_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    linked_by = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_user_link", x => x.id);
                });

            // ── tel_webhook_event : PARTITIONNÉE, écrite à la main ─────────────────────────
            //
            // Pas migrationBuilder.CreateTable : EF ne sait pas exprimer PARTITION BY, et c'est
            // la table la plus volumineuse de la famille — un événement par sonnerie, par
            // décroché et par fin d'appel, de chaque agent de chaque établissement.
            //
            // La clé primaire est (id, received_at) et non (id) parce que PostgreSQL exige la
            // clé de partition dans toute contrainte unique d'une table partitionnée. L'entité
            // porte la même clé, donc EF et la base disent la même chose.
            //
            // Les index créés plus bas par les CreateIndex scaffoldés deviennent des index
            // PARTITIONNÉS, hérités par chaque partition présente et future.
            migrationBuilder.Sql("""
                CREATE TABLE integration.tel_webhook_event (
                    id            uuid                     NOT NULL,
                    received_at   timestamp with time zone NOT NULL,
                    tenant_id     uuid                     NOT NULL,
                    connection_id uuid                     NOT NULL,
                    dedup_key     character varying(200)   NOT NULL,
                    event_type    character varying(60)    NOT NULL,
                    payload_enc   bytea                    NOT NULL,
                    processed_at  timestamp with time zone NULL,
                    outcome       character varying(200)   NULL,
                    CONSTRAINT pk_tel_webhook_event PRIMARY KEY (id, received_at)
                ) PARTITION BY RANGE (received_at);
                """);

            // Le mois courant et les trois suivants. Une INSERTION sans partition correspondante
            // ÉCHOUE — elle n'est routée nulle part, et ce n'est pas un avertissement — donc le
            // job qui entretient la fenêtre fait partie de la fonctionnalité, pas du ménage
            // autour d'elle. Voir EnsureTelephonyPartitionsJob.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    start_month date := date_trunc('month', now())::date;
                    i int;
                    from_bound date;
                    to_bound date;
                BEGIN
                    FOR i IN 0..3 LOOP
                        from_bound := start_month + (i || ' month')::interval;
                        to_bound   := start_month + ((i + 1) || ' month')::interval;

                        EXECUTE format(
                            'CREATE TABLE IF NOT EXISTS integration.tel_webhook_event_y%sm%s '
                            || 'PARTITION OF integration.tel_webhook_event '
                            || 'FOR VALUES FROM (%L) TO (%L);',
                            to_char(from_bound, 'YYYY'), to_char(from_bound, 'MM'),
                            to_char(from_bound, 'YYYY-MM-DD HH24:MI:SS') || '+00',
                            to_char(to_bound,   'YYYY-MM-DD HH24:MI:SS') || '+00');
                    END LOOP;
                END $$;
                """);


            migrationBuilder.CreateIndex(
                name: "ux_integration_connection_active_telephony",
                schema: "integration",
                table: "integration_connection",
                column: "tenant_id",
                unique: true,
                filter: "is_active AND family = 'Telephony'");

            migrationBuilder.CreateIndex(
                name: "ix_tel_call_entity",
                schema: "integration",
                table: "tel_call",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tel_call_from_number",
                schema: "integration",
                table: "tel_call",
                columns: new[] { "tenant_id", "from_number_bi" });

            migrationBuilder.CreateIndex(
                name: "ix_tel_call_user",
                schema: "integration",
                table: "tel_call",
                columns: new[] { "tenant_id", "crm_user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ux_tel_call_external",
                schema: "integration",
                table: "tel_call",
                columns: new[] { "tenant_id", "connection_id", "external_call_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tel_click_to_call_user",
                schema: "integration",
                table: "tel_click_to_call",
                columns: new[] { "tenant_id", "crm_user_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tel_recording_due",
                schema: "integration",
                table: "tel_recording",
                columns: new[] { "tenant_id", "retention_until" },
                filter: "status = 'Available' AND legal_hold = false");

            migrationBuilder.CreateIndex(
                name: "ux_tel_recording_call",
                schema: "integration",
                table: "tel_recording",
                columns: new[] { "tenant_id", "call_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tel_user_link_crm_user",
                schema: "integration",
                table: "tel_user_link",
                columns: new[] { "tenant_id", "connection_id", "crm_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tel_user_link_external_user",
                schema: "integration",
                table: "tel_user_link",
                columns: new[] { "tenant_id", "connection_id", "external_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tel_webhook_event_dedup",
                schema: "integration",
                table: "tel_webhook_event",
                columns: new[] { "tenant_id", "connection_id", "dedup_key", "received_at" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tel_call",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tel_call_outcome",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tel_click_to_call",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tel_recording",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tel_user_link",
                schema: "integration");

            // CASCADE : une table partitionnée ne se supprime pas tant que ses partitions
            // existent, et elles ont été créées hors du scaffolding.
            migrationBuilder.Sql("DROP TABLE IF EXISTS integration.tel_webhook_event CASCADE;");

            migrationBuilder.DropIndex(
                name: "ux_integration_connection_active_telephony",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.CreateIndex(
                name: "ux_integration_connection_active_core_banking",
                schema: "integration",
                table: "integration_connection",
                column: "tenant_id",
                unique: true,
                filter: "is_active AND family = 'CoreBanking'");
        }
    }
}
