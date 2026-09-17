using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransitionConditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "condition_json",
                schema: "workflow",
                table: "workflow_transitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_auto_generated",
                schema: "workflow",
                table: "workflow_transitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "condition_json",
                schema: "workflow",
                table: "workflow_transitions");

            migrationBuilder.DropColumn(
                name: "is_auto_generated",
                schema: "workflow",
                table: "workflow_transitions");
        }
    }
}
