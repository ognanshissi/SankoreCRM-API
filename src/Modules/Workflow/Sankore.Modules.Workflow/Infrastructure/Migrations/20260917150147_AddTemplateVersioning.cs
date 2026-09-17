using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "workflow",
                table: "workflow_templates",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "template_version",
                schema: "workflow",
                table: "workflow_instances",
                type: "integer",
                nullable: false,
                defaultValue: 1);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropIndex(
                name: "ix_workflow_templates_tenant_id_entity_type_version",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropColumn(
                name: "template_version",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates",
                columns: new[] { "tenant_id", "entity_type" },
                unique: true,
                filter: "\"IsActive\" = true");
        }
    }
}
