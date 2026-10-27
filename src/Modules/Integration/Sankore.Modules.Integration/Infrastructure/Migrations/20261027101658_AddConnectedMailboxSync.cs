using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedMailboxSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mail_last_error",
                schema: "integration",
                table: "cal_connected_account",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mail_synced_at",
                schema: "integration",
                table: "cal_connected_account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cal_mail_message",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    internet_message_id = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    thread_key = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    folder = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    has_attachments = table.Column<bool>(type: "boolean", nullable: false),
                    from_enc = table.Column<string>(type: "text", nullable: true),
                    to_enc = table.Column<string>(type: "text", nullable: true),
                    cc_enc = table.Column<string>(type: "text", nullable: true),
                    subject_enc = table.Column<string>(type: "text", nullable: true),
                    snippet_enc = table.Column<string>(type: "text", nullable: true),
                    body_enc = table.Column<string>(type: "text", nullable: true),
                    body_is_html = table.Column<bool>(type: "boolean", nullable: false),
                    body_truncated = table.Column<bool>(type: "boolean", nullable: false),
                    attachments_enc = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_cal_mail_message_cal_connected_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "integration",
                        principalTable: "cal_connected_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cal_mail_sync_state",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cursor = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_sync_state", x => x.id);
                    table.ForeignKey(
                        name: "fk_cal_mail_sync_state_cal_connected_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "integration",
                        principalTable: "cal_connected_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cal_mail_link",
                schema: "integration",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_link", x => new { x.message_id, x.entity_type, x.entity_id });
                    table.ForeignKey(
                        name: "fk_cal_mail_link_connected_mail_messages_message_id",
                        column: x => x.message_id,
                        principalSchema: "integration",
                        principalTable: "cal_mail_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_link_tenant_id_entity_type_entity_id_sent_at",
                schema: "integration",
                table: "cal_mail_link",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_message_account_id",
                schema: "integration",
                table: "cal_mail_message",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_message_tenant_id_account_id_thread_key",
                schema: "integration",
                table: "cal_mail_message",
                columns: new[] { "tenant_id", "account_id", "thread_key" });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_message_tenant_id_user_id_sent_at",
                schema: "integration",
                table: "cal_mail_message",
                columns: new[] { "tenant_id", "user_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ux_cal_mail_message_account_external",
                schema: "integration",
                table: "cal_mail_message",
                columns: new[] { "tenant_id", "account_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_sync_state_account_id",
                schema: "integration",
                table: "cal_mail_sync_state",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_cal_mail_sync_state_stream",
                schema: "integration",
                table: "cal_mail_sync_state",
                columns: new[] { "tenant_id", "account_id", "stream" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_mail_link",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cal_mail_sync_state",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cal_mail_message",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "mail_last_error",
                schema: "integration",
                table: "cal_connected_account");

            migrationBuilder.DropColumn(
                name: "mail_synced_at",
                schema: "integration",
                table: "cal_connected_account");
        }
    }
}
