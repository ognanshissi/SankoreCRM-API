using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPullStateToLeadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "consecutive_failures",
                schema: "leads",
                table: "lead_source_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_pull_at",
                schema: "leads",
                table: "lead_source_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_pull_cursor",
                schema: "leads",
                table: "lead_source_configs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consecutive_failures",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "last_pull_at",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "last_pull_cursor",
                schema: "leads",
                table: "lead_source_configs");
        }
    }
}
