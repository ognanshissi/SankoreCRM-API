using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInstanceStepKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "config_json",
                schema: "workflow",
                table: "workflow_instance_steps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "workflow",
                table: "workflow_instance_steps",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Validation");

            // Instances started before the copy existed take it from their definition, so the
            // catch-up at start (AutomaticStepsCatchUp) can find the ones stuck on a notification.
            migrationBuilder.Sql("""
                UPDATE workflow.workflow_instance_steps s
                SET kind = d.kind, config_json = d.config_json
                FROM workflow.workflow_step_definitions d
                WHERE d.id = s.step_definition_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "config_json",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropColumn(
                name: "kind",
                schema: "workflow",
                table: "workflow_instance_steps");
        }
    }
}
