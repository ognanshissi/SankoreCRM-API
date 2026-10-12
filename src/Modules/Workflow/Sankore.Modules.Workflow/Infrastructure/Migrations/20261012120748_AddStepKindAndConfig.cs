using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStepKindAndConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config_json",
                schema: "workflow",
                table: "workflow_step_definitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_blocking",
                schema: "workflow",
                table: "workflow_step_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "workflow",
                table: "workflow_step_definitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Validation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "config_json",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropColumn(
                name: "is_blocking",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "workflow",
                table: "workflow_step_definitions");
        }
    }
}
