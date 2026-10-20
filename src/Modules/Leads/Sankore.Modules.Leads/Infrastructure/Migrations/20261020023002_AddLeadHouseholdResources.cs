using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadHouseholdResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "household_housing_cost",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "household_monthly_income",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "household_other_debts",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "household_resources_currency",
                schema: "leads",
                table: "leads",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "household_resources_declared_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "household_housing_cost",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "household_monthly_income",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "household_other_debts",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "household_resources_currency",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "household_resources_declared_at",
                schema: "leads",
                table: "leads");
        }
    }
}
