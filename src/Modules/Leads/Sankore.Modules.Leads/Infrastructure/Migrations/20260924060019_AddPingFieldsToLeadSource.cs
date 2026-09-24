using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPingFieldsToLeadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_ping_at",
                schema: "leads",
                table: "lead_source_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_ping_origin",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unauthorized_origin_seen",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_ping_at",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "last_ping_origin",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "unauthorized_origin_seen",
                schema: "leads",
                table: "lead_source_configs");
        }
    }
}
