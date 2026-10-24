using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowCircuits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_version",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.AddColumn<string>(
                name: "circuit",
                schema: "workflow",
                table: "workflow_templates",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            // A project workflow created before circuits existed becomes the « projet » circuit: a
            // project step names its circuit, and an empty key would read as « none ».
            migrationBuilder.Sql(
                "UPDATE workflow.workflow_templates SET circuit = 'projet' WHERE entity_type = 'Opportunity';");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_circuit",
                schema: "workflow",
                table: "workflow_templates",
                columns: new[] { "tenant_id", "entity_type", "circuit" },
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_circuit_version",
                schema: "workflow",
                table: "workflow_templates",
                columns: new[] { "tenant_id", "entity_type", "circuit", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_circuit",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_circuit_version",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropColumn(
                name: "circuit",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates",
                columns: new[] { "tenant_id", "entity_type" },
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_version",
                schema: "workflow",
                table: "workflow_templates",
                columns: new[] { "tenant_id", "entity_type", "version" },
                unique: true);
        }
    }
}
