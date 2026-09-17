using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowActionsAndTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_actions_workflow_instances_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions");

            migrationBuilder.DropIndex(
                name: "ix_workflow_actions_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions");

            migrationBuilder.DropColumn(
                name: "workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions");

            migrationBuilder.CreateTable(
                name: "workflow_tasks",
                schema: "workflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_role_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Normal"),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completion_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_assigned_to_user_id_status",
                schema: "workflow",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "assigned_to_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_instance_id",
                schema: "workflow",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "instance_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_tasks",
                schema: "workflow");

            migrationBuilder.AddColumn<Guid>(
                name: "workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_actions_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                column: "workflow_instance_id");

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_actions_workflow_instances_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                column: "workflow_instance_id",
                principalSchema: "workflow",
                principalTable: "workflow_instances",
                principalColumn: "id");
        }
    }
}
