using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fields",
                schema: "leads",
                table: "project_templates",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "required_field_keys",
                schema: "leads",
                table: "project_template_steps",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                schema: "leads",
                table: "opportunities",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_attachments",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_attachments", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_attachments_opportunities_opportunity_id",
                        column: x => x.opportunity_id,
                        principalSchema: "leads",
                        principalTable: "opportunities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_attachments_opportunity_id_field_key",
                schema: "leads",
                table: "project_attachments",
                columns: new[] { "opportunity_id", "field_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_attachments",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "fields",
                schema: "leads",
                table: "project_templates");

            migrationBuilder.DropColumn(
                name: "required_field_keys",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                schema: "leads",
                table: "opportunities");
        }
    }
}
