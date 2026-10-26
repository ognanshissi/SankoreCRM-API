using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class HoldingSinglePerLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_product_holdings_live_per_line",
                schema: "customers",
                table: "product_holdings");

            migrationBuilder.AddColumn<bool>(
                name: "single_per_line",
                schema: "customers",
                table: "product_holdings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_product_holdings_live_per_line",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "client_id", "product_line_id" },
                unique: true,
                filter: "status IN ('Draft', 'PendingSignature', 'PendingSync', 'Active') AND origin = 'CrmSubscription' AND single_per_line");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_product_holdings_live_per_line",
                schema: "customers",
                table: "product_holdings");

            migrationBuilder.DropColumn(
                name: "single_per_line",
                schema: "customers",
                table: "product_holdings");

            migrationBuilder.CreateIndex(
                name: "ux_product_holdings_live_per_line",
                schema: "customers",
                table: "product_holdings",
                columns: new[] { "tenant_id", "client_id", "product_line_id" },
                unique: true,
                filter: "status IN ('Draft', 'PendingSignature', 'PendingSync', 'Active') AND origin = 'CrmSubscription'");
        }
    }
}
