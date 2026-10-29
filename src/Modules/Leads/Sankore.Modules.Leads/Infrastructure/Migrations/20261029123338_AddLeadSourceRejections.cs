using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadSourceRejections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_rejected_at",
                schema: "leads",
                table: "lead_source_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_rejected_reason",
                schema: "leads",
                table: "lead_source_configs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_rejected_at",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "last_rejected_reason",
                schema: "leads",
                table: "lead_source_configs");
        }
    }
}
