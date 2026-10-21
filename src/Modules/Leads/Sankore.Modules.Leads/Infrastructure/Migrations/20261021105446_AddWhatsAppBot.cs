using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppBot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "bot_collected_fields",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<int>(
                name: "bot_unanswered_turns",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "whatsapp_bot_profiles",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    welcome_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    out_of_hours_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    faq = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    fields_to_collect = table.Column<List<string>>(type: "text[]", nullable: false),
                    agent_hours = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    escalation_keywords = table.Column<List<string>>(type: "text[]", nullable: false),
                    max_turns_without_answer = table.Column<int>(type: "integer", nullable: false),
                    window_expiry_threshold_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_bot_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "whatsapp_bot_turns",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    profile_version = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_enc = table.Column<string>(type: "text", nullable: true),
                    output_enc = table.Column<string>(type: "text", nullable: true),
                    tool_calls_enc = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    escalation_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    answered = table.Column<bool>(type: "boolean", nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: true),
                    output_tokens = table.Column<int>(type: "integer", nullable: true),
                    error_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    reply_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_bot_turns", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_bot_profiles_version",
                schema: "leads",
                table: "whatsapp_bot_profiles",
                columns: new[] { "tenant_id", "product_line_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_bot_turns_tenant_id_conversation_id_created_at",
                schema: "leads",
                table: "whatsapp_bot_turns",
                columns: new[] { "tenant_id", "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_whatsapp_bot_turns_inbound_message",
                schema: "leads",
                table: "whatsapp_bot_turns",
                columns: new[] { "tenant_id", "inbound_message_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_bot_profiles",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "whatsapp_bot_turns",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "bot_collected_fields",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "bot_unanswered_turns",
                schema: "leads",
                table: "whatsapp_conversations");
        }
    }
}
