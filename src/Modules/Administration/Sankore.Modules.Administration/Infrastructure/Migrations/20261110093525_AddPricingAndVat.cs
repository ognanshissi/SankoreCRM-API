using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingAndVat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Quote lines, lot 1 (2026-11-10). The defaults below exist for the rows already there
            // and are deliberately NOT in the model (see CompanyInfoConfiguration): existing
            // products are « Standard », existing institutions start at Côte d'Ivoire's 18 % / 9 %.
            migrationBuilder.AddColumn<string>(
                name: "price_unit",
                schema: "administration",
                table: "product_specialities",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "unit_price",
                schema: "administration",
                table: "product_specialities",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vat_category",
                schema: "administration",
                table: "product_specialities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<decimal>(
                name: "max_discount_percent",
                schema: "administration",
                table: "company_info",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "vat_reduced_rate",
                schema: "administration",
                table: "company_info",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 9m);

            migrationBuilder.AddColumn<decimal>(
                name: "vat_standard_rate",
                schema: "administration",
                table: "company_info",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: false,
                defaultValue: 18m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "price_unit",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "unit_price",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "vat_category",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "max_discount_percent",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "vat_reduced_rate",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "vat_standard_rate",
                schema: "administration",
                table: "company_info");
        }
    }
}
