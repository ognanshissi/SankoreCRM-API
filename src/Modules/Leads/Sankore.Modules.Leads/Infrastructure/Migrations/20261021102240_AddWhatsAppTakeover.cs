using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTakeover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "close_reason_enc",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "closed_by",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "escalation_reason",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "escalation_summary_enc",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_since",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "taken_over_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "takeover_breached_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "takeover_deadline",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_agent_id",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "window_warning_sent_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_owner_user_id",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_status_takeover_deadline",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "status", "takeover_deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_conversations_tenant_id_target_agent_id",
                schema: "leads",
                table: "whatsapp_conversations",
                columns: new[] { "tenant_id", "target_agent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_whatsapp_conversations_tenant_id_owner_user_id",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_conversations_tenant_id_status_takeover_deadline",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropIndex(
                name: "ix_whatsapp_conversations_tenant_id_target_agent_id",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "close_reason_enc",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "closed_by",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "escalation_reason",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "escalation_summary_enc",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "pending_since",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "taken_over_at",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "takeover_breached_at",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "takeover_deadline",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "target_agent_id",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "window_warning_sent_at",
                schema: "leads",
                table: "whatsapp_conversations");
        }
    }
}
