using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectTemplateVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "template_version_id",
                schema: "leads",
                table: "opportunities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_template_versions",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    is_baseline = table.Column<bool>(type: "boolean", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_template_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_template_versions_project_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "leads",
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_template_versions_template_id_number",
                schema: "leads",
                table: "project_template_versions",
                columns: new[] { "template_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_template_versions",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "template_version_id",
                schema: "leads",
                table: "opportunities");
        }
    }
}
