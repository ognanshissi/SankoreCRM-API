using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityAttachmentsCtiVisitFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attachments",
                schema: "leads",
                table: "lead_activities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cti_call_reference",
                schema: "leads",
                table: "lead_activities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_system_generated",
                schema: "leads",
                table: "lead_activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "retention_expires_at",
                schema: "leads",
                table: "lead_activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "visit_lat",
                schema: "leads",
                table: "lead_activities",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "visit_lng",
                schema: "leads",
                table: "lead_activities",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "visit_photo_reference",
                schema: "leads",
                table: "lead_activities",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachments",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "cti_call_reference",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "is_system_generated",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "retention_expires_at",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "visit_lat",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "visit_lng",
                schema: "leads",
                table: "lead_activities");

            migrationBuilder.DropColumn(
                name: "visit_photo_reference",
                schema: "leads",
                table: "lead_activities");
        }
    }
}
