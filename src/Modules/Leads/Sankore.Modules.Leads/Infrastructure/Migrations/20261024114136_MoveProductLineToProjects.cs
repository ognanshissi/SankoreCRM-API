using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveProductLineToProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The sale's product line moves from the lead to its project (product owner, 2026-10-24).
            migrationBuilder.AddColumn<Guid>(
                name: "product_line_id",
                schema: "leads",
                table: "opportunities",
                type: "uuid",
                nullable: true);

            // A project whose product IS the one its lead asked for inherits the line the lead had
            // resolved. Any other project keeps none: its label is resolved on read (and recorded on
            // its next product change) — copying the lead's line onto a different product would hand
            // that sale another product's KYC policy.
            migrationBuilder.Sql("""
                UPDATE leads.opportunities o
                SET product_line_id = l.product_line_id
                FROM leads.leads l
                WHERE o.lead_id = l.id
                  AND o.product_line_id IS NULL
                  AND l.product_line_id IS NOT NULL
                  AND lower(btrim(o.product)) = lower(btrim(l.interested_product));
                """);

            migrationBuilder.DropColumn(
                name: "product_line_id",
                schema: "leads",
                table: "leads");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "product_line_id",
                schema: "leads",
                table: "opportunities");

            migrationBuilder.AddColumn<Guid>(
                name: "product_line_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);
        }
    }
}
