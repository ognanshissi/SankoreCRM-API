using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppComplaintIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "excluded_from_model",
                schema: "leads",
                table: "whatsapp_messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "whatsapp_complaint_intakes",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failed_identifications = table.Column<int>(type: "integer", nullable: false),
                    pending_client_number_enc = table.Column<string>(type: "text", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description_enc = table.Column<string>(type: "text", nullable: true),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    end_reason = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    complaint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    complaint_reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whatsapp_complaint_intakes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whatsapp_complaint_intakes_tenant_id_conversation_id_last_a",
                schema: "leads",
                table: "whatsapp_complaint_intakes",
                columns: new[] { "tenant_id", "conversation_id", "last_activity_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_complaint_intakes",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "excluded_from_model",
                schema: "leads",
                table: "whatsapp_messages");
        }
    }
}
