using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameProductTypeToProductCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_product_type",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.RenameColumn(
                name: "product_type",
                schema: "leads",
                table: "qualification_templates",
                newName: "product_category");

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_category",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_category" },
                unique: true,
                filter: "product_category IS NOT NULL AND status = 'Published'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_product_category",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.RenameColumn(
                name: "product_category",
                schema: "leads",
                table: "qualification_templates",
                newName: "product_type");

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_type",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_type" },
                unique: true,
                filter: "product_type IS NOT NULL AND status = 'Published'");
        }
    }
}
