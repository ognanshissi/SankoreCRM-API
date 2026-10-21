using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_address",
                schema: "integration",
                table: "integration_connection",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "msg_webhook_event",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_enc = table.Column<byte[]>(type: "bytea", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_msg_webhook_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_integration_connection_active_channel_address",
                schema: "integration",
                table: "integration_connection",
                column: "channel_address",
                unique: true,
                filter: "is_active AND channel_address IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_msg_webhook_event_pending",
                schema: "integration",
                table: "msg_webhook_event",
                columns: new[] { "tenant_id", "processed_at", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_msg_webhook_event_dedup",
                schema: "integration",
                table: "msg_webhook_event",
                columns: new[] { "tenant_id", "connection_id", "dedup_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "msg_webhook_event",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ux_integration_connection_active_channel_address",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "channel_address",
                schema: "integration",
                table: "integration_connection");
        }
    }
}
