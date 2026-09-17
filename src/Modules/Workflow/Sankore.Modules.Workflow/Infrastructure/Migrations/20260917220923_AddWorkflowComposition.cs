using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_instance_id",
                schema: "workflow",
                table: "workflow_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "waiting_for_child_id",
                schema: "workflow",
                table: "workflow_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instances_parent_instance_id",
                schema: "workflow",
                table: "workflow_instances",
                column: "parent_instance_id",
                filter: "parent_instance_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_instances_parent_instance_id",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "parent_instance_id",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "waiting_for_child_id",
                schema: "workflow",
                table: "workflow_instances");
        }
    }
}
