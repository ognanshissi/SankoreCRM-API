using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskReassignmentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_reassignments",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reassigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sla_extended = table.Column<bool>(type: "boolean", nullable: false),
                    new_sla_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_reassignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_reassignments_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_reassignments_task_id",
                schema: "leads",
                table: "task_reassignments",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_reassignments_tenant_id_new_agent_id",
                schema: "leads",
                table: "task_reassignments",
                columns: new[] { "tenant_id", "new_agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_reassignments_tenant_id_task_id_reassigned_at",
                schema: "leads",
                table: "task_reassignments",
                columns: new[] { "tenant_id", "task_id", "reassigned_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_reassignments",
                schema: "leads");
        }
    }
}
