using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectDocumentTrail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_attachment_id",
                schema: "leads",
                table: "whatsapp_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_sent_at",
                schema: "leads",
                table: "project_attachments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_sent_channel",
                schema: "leads",
                table: "project_attachments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "project_attachment_id",
                schema: "leads",
                table: "whatsapp_messages");

            migrationBuilder.DropColumn(
                name: "last_sent_at",
                schema: "leads",
                table: "project_attachments");

            migrationBuilder.DropColumn(
                name: "last_sent_channel",
                schema: "leads",
                table: "project_attachments");
        }
    }
}
