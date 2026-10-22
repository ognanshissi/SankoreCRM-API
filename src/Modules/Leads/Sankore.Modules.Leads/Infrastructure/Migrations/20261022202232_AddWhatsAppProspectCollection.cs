using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppProspectCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "bot_lead_ready_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "collecting_prospect",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pending_lead_fields_enc",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "fields_required_for_lead",
                schema: "leads",
                table: "whatsapp_bot_profiles",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bot_lead_ready_at",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "collecting_prospect",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "pending_lead_fields_enc",
                schema: "leads",
                table: "whatsapp_conversations");

            migrationBuilder.DropColumn(
                name: "fields_required_for_lead",
                schema: "leads",
                table: "whatsapp_bot_profiles");
        }
    }
}
