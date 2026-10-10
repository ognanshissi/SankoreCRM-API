using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBeneficiaryPolicyToProductLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_multiple_holdings",
                schema: "administration",
                table: "product_specialities",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "beneficiary_policy",
                schema: "administration",
                table: "product_specialities",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_multiple_holdings",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.DropColumn(
                name: "beneficiary_policy",
                schema: "administration",
                table: "product_specialities");
        }
    }
}
