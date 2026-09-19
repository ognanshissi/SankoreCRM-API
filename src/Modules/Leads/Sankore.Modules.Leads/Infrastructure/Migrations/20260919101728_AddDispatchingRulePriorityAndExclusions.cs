using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchingRulePriorityAndExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_dispatching_rules_tenant_id_is_active",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.AddColumn<string>(
                name: "excluded_agent_ids",
                schema: "leads",
                table: "dispatching_rules",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "priority",
                schema: "leads",
                table: "dispatching_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_dispatching_rules_tenant_id_strategy_is_active_priority",
                schema: "leads",
                table: "dispatching_rules",
                columns: new[] { "tenant_id", "strategy", "is_active", "priority" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_dispatching_rules_tenant_id_strategy_is_active_priority",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.DropColumn(
                name: "excluded_agent_ids",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.DropColumn(
                name: "priority",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.CreateIndex(
                name: "ix_dispatching_rules_tenant_id_is_active",
                schema: "leads",
                table: "dispatching_rules",
                columns: new[] { "tenant_id", "is_active" });
        }
    }
}
