using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSmsChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sms_delivery_logs",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbox_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    masked_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    raw_payload = table.Column<string>(type: "text", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_delivery_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sms_outbox_messages",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    recipient_phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    template_data_json = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    segment_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sms_templates",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sms_delivery_logs_outbox_message_id",
                schema: "notifications",
                table: "sms_delivery_logs",
                column: "outbox_message_id");

            migrationBuilder.CreateIndex(
                name: "ix_sms_delivery_logs_tenant_id_recorded_at",
                schema: "notifications",
                table: "sms_delivery_logs",
                columns: new[] { "tenant_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sms_outbox_messages_idempotency_key",
                schema: "notifications",
                table: "sms_outbox_messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sms_outbox_messages_provider_id",
                schema: "notifications",
                table: "sms_outbox_messages",
                column: "provider_message_id",
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sms_outbox_messages_status_created_at",
                schema: "notifications",
                table: "sms_outbox_messages",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sms_outbox_messages_tenant_id_status",
                schema: "notifications",
                table: "sms_outbox_messages",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_sms_templates_resolution",
                schema: "notifications",
                table: "sms_templates",
                columns: new[] { "tenant_id", "template_key", "locale", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sms_delivery_logs",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "sms_outbox_messages",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "sms_templates",
                schema: "notifications");
        }
    }
}
