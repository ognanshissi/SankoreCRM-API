using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStatesAndTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "code",
                schema: "workflow",
                table: "workflow_step_definitions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "STEP_0");

            migrationBuilder.AddColumn<string>(
                name: "state_type",
                schema: "workflow",
                table: "workflow_step_definitions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Approval");

            migrationBuilder.AddColumn<Guid>(
                name: "current_state_id",
                schema: "workflow",
                table: "workflow_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "workflow_transitions",
                schema: "workflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_state_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_terminal_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    event_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_workflow_transitions_workflow_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "workflow",
                        principalTable: "workflow_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_transitions_template_id_from_state_id_event_code_p",
                schema: "workflow",
                table: "workflow_transitions",
                columns: new[] { "template_id", "from_state_id", "event_code", "priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_transitions",
                schema: "workflow");

            migrationBuilder.DropColumn(
                name: "code",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropColumn(
                name: "state_type",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropColumn(
                name: "current_state_id",
                schema: "workflow",
                table: "workflow_instances");
        }
    }
}
