using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualificationTemplateSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_is_active",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "leads",
                table: "qualification_templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "leads",
                table: "qualification_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<int>(
                name: "version",
                schema: "leads",
                table: "qualification_templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "help_text",
                schema: "leads",
                table: "qualification_questions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "max_value",
                schema: "leads",
                table: "qualification_questions",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "min_value",
                schema: "leads",
                table: "qualification_questions",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "placeholder_text",
                schema: "leads",
                table: "qualification_questions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rules_json",
                schema: "leads",
                table: "qualification_questions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "section_id",
                schema: "leads",
                table: "qualification_questions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "qualification_sections",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qualification_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_qualification_sections_qualification_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "leads",
                        principalTable: "qualification_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_status",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_sections_template_id_order",
                schema: "leads",
                table: "qualification_sections",
                columns: new[] { "template_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qualification_sections",
                schema: "leads");

            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_status",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "help_text",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.DropColumn(
                name: "max_value",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.DropColumn(
                name: "min_value",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.DropColumn(
                name: "placeholder_text",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.DropColumn(
                name: "rules_json",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.DropColumn(
                name: "section_id",
                schema: "leads",
                table: "qualification_questions");

            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "leads",
                table: "qualification_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_is_active",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "is_active" });
        }
    }
}
