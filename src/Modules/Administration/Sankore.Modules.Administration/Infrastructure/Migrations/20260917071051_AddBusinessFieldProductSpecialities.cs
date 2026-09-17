using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessFieldProductSpecialities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "business_platform_name",
                schema: "administration",
                table: "product_specialities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "business_product_id",
                schema: "administration",
                table: "product_specialities",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "business_platform_name",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "business_product_id",
                schema: "administration",
                table: "product_specialities");
        }
    }
}
