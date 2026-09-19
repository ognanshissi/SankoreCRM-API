using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmTasksAndGenerationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "weights_agency",
                schema: "leads",
                table: "dispatching_rules",
                newName: "weight_agency");

            migrationBuilder.CreateTable(
                name: "crm_tasks",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sla_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger_event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    trigger_event_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_crm_tasks_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "task_generation_rules",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger_event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    task_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title_template = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description_template = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sla_duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    due_duration = table.Column<TimeSpan>(type: "interval", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_generation_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_assigned_agent_id_status",
                schema: "leads",
                table: "crm_tasks",
                columns: new[] { "assigned_agent_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_lead_id_status",
                schema: "leads",
                table: "crm_tasks",
                columns: new[] { "lead_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_tenant_id_status_due_at",
                schema: "leads",
                table: "crm_tasks",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_task_generation_rules_tenant_id_trigger_event_type_is_active",
                schema: "leads",
                table: "task_generation_rules",
                columns: new[] { "tenant_id", "trigger_event_type", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_tasks",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "task_generation_rules",
                schema: "leads");

            migrationBuilder.RenameColumn(
                name: "weight_agency",
                schema: "leads",
                table: "dispatching_rules",
                newName: "weights_agency");
        }
    }
}
