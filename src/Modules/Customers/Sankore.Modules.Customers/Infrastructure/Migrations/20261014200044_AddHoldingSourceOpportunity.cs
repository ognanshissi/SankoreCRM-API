using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldingSourceOpportunity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_opportunity_id",
                schema: "customers",
                table: "product_holdings",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_opportunity_id",
                schema: "customers",
                table: "product_holdings");
        }
    }
}
