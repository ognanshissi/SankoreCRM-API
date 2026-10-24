using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectStepConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "checklist",
                schema: "leads",
                table: "project_template_steps",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<bool>(
                name: "requires_amount",
                schema: "leads",
                table: "project_template_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "requires_close_date",
                schema: "leads",
                table: "project_template_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "workflow_circuit",
                schema: "leads",
                table: "project_template_steps",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "checked_checklist_items",
                schema: "leads",
                table: "opportunities",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "workflow_runs",
                schema: "leads",
                table: "opportunities",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "checklist",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "requires_amount",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "requires_close_date",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "workflow_circuit",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "checked_checklist_items",
                schema: "leads",
                table: "opportunities");

            migrationBuilder.DropColumn(
                name: "workflow_runs",
                schema: "leads",
                table: "opportunities");
        }
    }
}
