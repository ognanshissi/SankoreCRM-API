using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_purged_at",
                schema: "leads",
                table: "whatsapp_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "media_sha256",
                schema: "leads",
                table: "whatsapp_messages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "media_size_bytes",
                schema: "leads",
                table: "whatsapp_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "media_storage_ref",
                schema: "leads",
                table: "whatsapp_messages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "media_stored_at",
                schema: "leads",
                table: "whatsapp_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            // Conversations anonymised before this column existed: their blind index was replaced
            // by « anonymized:{id} » (WhatsAppConversation.Anonymize), the one plaintext trace of it.
            // Without the backfill their media would wait for the media clock instead of going now.
            migrationBuilder.Sql(
                "UPDATE leads.whatsapp_conversations SET anonymized_at = now() "
                + "WHERE anonymized_at IS NULL AND contact_phone_blind_index LIKE 'anonymized:%';");

            migrationBuilder.CreateTable(
                name: "whatsapp_handovers",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    from_bot = table.Column<bool>(type: "boolean", nullable: false),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    takeover_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    taken_over_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_handovers", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_messages_tenant_id_media_stored_at",
                schema: "leads",
                table: "whatsapp_messages",
                columns: new[] { "tenant_id", "media_stored_at" },
                filter: "media_storage_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_lead_id_last_message_at",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "lead_id", "last_message_at" },
                filter: "anonymized_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_handovers_tenant_id_conversation_id",
                schema: "leads",
                table: "whatsapp_handovers",
                columns: new[] { "tenant_id", "conversation_id" },
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_handovers_tenant_id_escalated_at",
                schema: "leads",
                table: "whatsapp_handovers",
                columns: new[] { "tenant_id", "escalated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_handovers",
                schema: "leads");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_messages_tenant_id_media_stored_at",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_conversations_tenant_id_lead_id_last_message_at",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "media_purged_at",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "media_sha256",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "media_size_bytes",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "media_storage_ref",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "media_stored_at",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                schema: "leads",
                table: "whatsapp_conversations");
        }
    }
}
