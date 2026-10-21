using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaWarningAndTaskSlaHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "previous_sla_deadline",
                schema: "leads",
                table: "task_reassignments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sla_warned_at",
                schema: "leads",
                table: "lead_assignments",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "previous_sla_deadline",
                schema: "leads",
                table: "task_reassignments");

            migrationBuilder.DropColumn(
                name: "sla_warned_at",
                schema: "leads",
                table: "lead_assignments");
        }
    }
}
