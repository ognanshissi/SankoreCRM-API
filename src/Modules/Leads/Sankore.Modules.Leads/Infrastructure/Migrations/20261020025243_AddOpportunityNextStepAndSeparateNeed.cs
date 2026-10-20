using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunityNextStepAndSeparateNeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "next_step",
                schema: "leads",
                table: "opportunities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "next_step_due_on",
                schema: "leads",
                table: "opportunities",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "separate_need_confirmed_at",
                schema: "leads",
                table: "opportunities",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_step",
                schema: "leads",
                table: "opportunities");

            migrationBuilder.DropColumn(
                name: "next_step_due_on",
                schema: "leads",
                table: "opportunities");

            migrationBuilder.DropColumn(
                name: "separate_need_confirmed_at",
                schema: "leads",
                table: "opportunities");
        }
    }
}
