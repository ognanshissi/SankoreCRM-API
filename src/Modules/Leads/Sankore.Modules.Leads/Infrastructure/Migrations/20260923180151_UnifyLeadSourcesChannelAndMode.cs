using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyLeadSourcesChannelAndMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.RenameIndex(
                name: "ix_lead_source_configs_tenant_id_code",
                schema: "leads",
                table: "lead_source_configs",
                newName: "ux_lead_sources_code");

            migrationBuilder.AddColumn<string>(
                name: "channel_type",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "cost_currency",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cost_per_lead",
                schema: "leads",
                table: "lead_source_configs",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "dedup_window_days",
                schema: "leads",
                table: "lead_source_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "platform_connection_id",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_key",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                schema: "leads",
                table: "lead_source_configs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            // ── Data migration (idempotent) ────────────────────────────────

            // 1. Backfill existing v1 rows with Internal channel/mode
            migrationBuilder.Sql(
                """
                UPDATE leads.lead_source_configs
                SET channel_type = 'WalkIn',
                    mode = 'Internal',
                    status = 'Active',
                    dedup_window_days = 30
                WHERE channel_type = '' OR channel_type IS NULL;
                """);

            // 2. Seed one Internal system source per internal channel per tenant.
            //    Idempotent: INSERT ... ON CONFLICT DO NOTHING on (tenant_id, code).
            migrationBuilder.Sql(
                """
                INSERT INTO leads.lead_source_configs
                    (id, tenant_id, code, label, channel_type, mode, status,
                     dedup_window_days, is_system, display_order, created_at)
                SELECT
                    gen_random_uuid(),
                    t.tenant_id,
                    ch.code,
                    ch.label,
                    ch.channel_type,
                    'Internal',
                    'Active',
                    30,
                    true,
                    ch.display_order,
                    now()
                FROM (SELECT DISTINCT tenant_id FROM leads.lead_source_configs) t
                CROSS JOIN (VALUES
                    ('MOBILE_AGENT', 'Agent Mobile',        'MobileAgent',     1),
                    ('WALK_IN',      'Visite en agence',    'WalkIn',          2),
                    ('REFERRAL',     'Parrainage',          'Referral',        3),
                    ('FILE_IMPORT',  'Import fichier',      'FileImport',      4),
                    ('INBOUND_CALL', 'Appel entrant',       'InboundCall',     5),
                    ('SMS_USSD',     'Campagne SMS/USSD',   'SmsUssdCampaign', 6)
                ) AS ch(code, label, channel_type, display_order)
                ON CONFLICT (tenant_id, code) DO NOTHING;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_lead_sources_pull",
                schema: "leads",
                table: "lead_source_configs",
                columns: new[] { "tenant_id", "mode", "status" },
                filter: "mode = 'ScheduledPull' AND status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_lead_sources_public_key",
                schema: "leads",
                table: "lead_source_configs",
                column: "public_key",
                unique: true,
                filter: "public_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lead_sources_pull",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropIndex(
                name: "ux_lead_sources_public_key",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "channel_type",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "cost_currency",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "cost_per_lead",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "dedup_window_days",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "mode",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "platform_connection_id",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "public_key",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "settings",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.RenameIndex(
                name: "ux_lead_sources_code",
                schema: "leads",
                table: "lead_source_configs",
                newName: "ix_lead_source_configs_tenant_id_code");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "leads",
                table: "lead_source_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
