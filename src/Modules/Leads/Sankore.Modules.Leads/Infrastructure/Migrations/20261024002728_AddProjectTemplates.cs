using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "step_id",
                schema: "leads",
                table: "opportunities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_templates",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    product_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_template_steps",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    phase = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    probability = table.Column<int>(type: "integer", nullable: false),
                    suggestion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_template_steps", x => new { x.template_id, x.id });
                    table.ForeignKey(
                        name: "fk_project_template_steps_project_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "leads",
                        principalTable: "project_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_project_templates_category",
                schema: "leads",
                table: "project_templates",
                columns: new[] { "tenant_id", "category" },
                unique: true,
                filter: "category IS NOT NULL AND product_code IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_project_templates_default",
                schema: "leads",
                table: "project_templates",
                column: "tenant_id",
                unique: true,
                filter: "category IS NULL AND product_code IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_project_templates_product",
                schema: "leads",
                table: "project_templates",
                columns: new[] { "tenant_id", "product_code" },
                unique: true,
                filter: "product_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_template_steps",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "project_templates",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "step_id",
                schema: "leads",
                table: "opportunities");
        }
    }
}
