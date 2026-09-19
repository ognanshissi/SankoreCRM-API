using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDeclinesAndExclusionTtl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "decline_exclusion_ttl",
                schema: "leads",
                table: "dispatching_rules",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 30, 0, 0));

            migrationBuilder.CreateTable(
                name: "task_declines",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    declined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_declines", x => x.id);
                    table.ForeignKey(
                        name: "fk_task_declines_crm_tasks_task_id",
                        column: x => x.task_id,
                        principalSchema: "leads",
                        principalTable: "crm_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_declines_task_id",
                schema: "leads",
                table: "task_declines",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_declines_tenant_id_agent_id",
                schema: "leads",
                table: "task_declines",
                columns: new[] { "tenant_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_task_declines_tenant_id_task_id_declined_at",
                schema: "leads",
                table: "task_declines",
                columns: new[] { "tenant_id", "task_id", "declined_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_declines",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "decline_exclusion_ttl",
                schema: "leads",
                table: "dispatching_rules");
        }
    }
}
