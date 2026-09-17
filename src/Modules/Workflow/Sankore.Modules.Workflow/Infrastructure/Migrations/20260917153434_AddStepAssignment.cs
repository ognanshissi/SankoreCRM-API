using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStepAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "assigned_to_user_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_instance_steps_tenant_id_assigned_to_user_id_status",
                schema: "workflow",
                table: "workflow_instance_steps",
                columns: new[] { "tenant_id", "assigned_to_user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_instance_steps_tenant_id_assigned_to_user_id_status",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropColumn(
                name: "assigned_to_user_id",
                schema: "workflow",
                table: "workflow_instance_steps");
        }
    }
}
