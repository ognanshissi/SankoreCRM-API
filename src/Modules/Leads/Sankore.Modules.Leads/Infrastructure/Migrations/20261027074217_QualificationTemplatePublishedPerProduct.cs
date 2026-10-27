using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QualificationTemplatePublishedPerProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_qualification_templates_tenant_id_product_category",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.CreateIndex(
                name: "ux_qualification_templates_published_scope",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_category", "product_code" },
                unique: true,
                filter: "status = 'Published'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_qualification_templates_published_scope",
                schema: "leads",
                table: "qualification_templates");

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_category",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_category" },
                unique: true,
                filter: "product_category IS NOT NULL AND status = 'Published'");
        }
    }
}
