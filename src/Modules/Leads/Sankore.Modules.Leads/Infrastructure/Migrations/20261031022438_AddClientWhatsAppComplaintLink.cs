using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientWhatsAppComplaintLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "complaint_id",
                schema: "leads",
                table: "whatsapp_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "opted_out_at",
                schema: "leads",
                table: "whatsapp_conversations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "complaint_id",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "opted_out_at",
                schema: "leads",
                table: "whatsapp_conversations");
        }
    }
}
