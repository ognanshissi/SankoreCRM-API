using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSlaDueAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add SLA deadline columns to workflow_instance_steps (US-M12-WORKFLOW-001).
            // sla_hours: copied from the step definition at instance creation time.
            // due_at:    computed when the step enters AwaitingApproval (CreatedAt + sla_hours).
            migrationBuilder.AddColumn<int>(
                name: "sla_hours",
                schema: "workflow",
                table: "workflow_instance_steps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "due_at",
                schema: "workflow",
                table: "workflow_instance_steps",
                type: "timestamp with time zone",
                nullable: true);

            // Partial index: the SLA checker job queries only AwaitingApproval rows with a DueAt.
            migrationBuilder.Sql(@"
                CREATE INDEX ix_workflow_instance_steps_sla
                ON workflow.workflow_instance_steps (due_at)
                WHERE status = 'AwaitingApproval' AND due_at IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS workflow.ix_workflow_instance_steps_sla;
            ");

            migrationBuilder.DropColumn(
                name: "due_at",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropColumn(
                name: "sla_hours",
                schema: "workflow",
                table: "workflow_instance_steps");
        }
    }
}
