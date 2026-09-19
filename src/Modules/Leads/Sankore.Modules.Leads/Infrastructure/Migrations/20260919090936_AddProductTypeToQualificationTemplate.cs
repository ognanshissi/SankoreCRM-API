using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductTypeToQualificationTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_product_name",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "product_name",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.AddColumn<string>(
                name: "product_type",
                schema: "leads",
                table: "qualification_templates",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_type",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_type" },
                unique: true,
                filter: "product_type IS NOT NULL AND status = 'Published'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_product_type",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.DropColumn(
                name: "product_type",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.AddColumn<string>(
                name: "product_name",
                schema: "leads",
                table: "qualification_templates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_name",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_name" },
                filter: "product_name IS NOT NULL");
        }
    }
}
