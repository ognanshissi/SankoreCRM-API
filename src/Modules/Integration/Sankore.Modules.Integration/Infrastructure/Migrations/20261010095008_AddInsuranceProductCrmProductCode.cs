using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInsuranceProductCrmProductCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "crm_product_code",
                schema: "integration",
                table: "ins_product",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_product_crm_product",
                schema: "integration",
                table: "ins_product",
                columns: new[] { "tenant_id", "crm_product_code" },
                filter: "crm_product_code IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ins_product_crm_product",
                schema: "integration",
                table: "ins_product");

            migrationBuilder.DropColumn(
                name: "crm_product_code",
                schema: "integration",
                table: "ins_product");
        }
    }
}
