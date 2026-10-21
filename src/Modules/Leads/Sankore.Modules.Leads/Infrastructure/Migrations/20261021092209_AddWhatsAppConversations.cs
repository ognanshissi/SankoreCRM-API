using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whatsapp_conversations",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_phone_enc = table.Column<string>(type: "text", nullable: false),
                    contact_phone_blind_index = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    contact_name_enc = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ambiguous_match = table.Column<bool>(type: "boolean", nullable: false),
                    window_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_inbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_template_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    unread_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                    // No xmin column: WhatsAppConversation.Version is a rowversion, which Npgsql
                    // binds to PostgreSQL's xmin SYSTEM column. EF scaffolds it as a real column,
                    // and PostgreSQL refuses that DDL — see XminIsNeverScaffoldedTests.
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_messages",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    author = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    body_enc = table.Column<string>(type: "text", nullable: true),
                    template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    template_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    template_language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    template_parameters_enc = table.Column<string>(type: "text", nullable: true),
                    media_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    media_mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    media_file_name_enc = table.Column<string>(type: "text", nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reply_to_provider_message_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    error_detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_templates",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_template_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    header_text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    body_text = table.Column<string>(type: "text", nullable: false),
                    footer_text = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    buttons = table.Column<List<string>>(type: "text[]", nullable: false),
                    variable_count = table.Column<int>(type: "integer", nullable: false),
                    product_line_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    variable_mappings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    configured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    configured_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_client_id",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_lead_id",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_status_last_message_at",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "status", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_conversations_contact",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "connection_id", "contact_phone_blind_index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_messages_conversation_id_occurred_at",
                schema: "leads",
                table: "whatsapp_messages",
                columns: new[] { "conversation_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_messages_provider_id",
                schema: "leads",
                table: "whatsapp_messages",
                columns: new[] { "tenant_id", "provider_message_id" },
                unique: true,
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_templates_name_language",
                schema: "leads",
                table: "whatsapp_templates",
                columns: new[] { "tenant_id", "connection_id", "name", "language" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_conversations",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "whatsapp_messages",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "whatsapp_templates",
                schema: "leads");
        }
    }
}
