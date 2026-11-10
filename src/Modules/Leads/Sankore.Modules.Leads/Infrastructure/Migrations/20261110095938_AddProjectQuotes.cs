using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_quotes",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    global_discount_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    global_discount_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    subtotal_excl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    global_discount_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_excl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_vat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_incl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    effective_discount_percent = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    discount_ceiling_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    approval_status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_quotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_quotes_opportunities_opportunity_id",
                        column: x => x.opportunity_id,
                        principalSchema: "leads",
                        principalTable: "opportunities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_quote_lines",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    unit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    catalogue_unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    discount_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    discount_value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    vat_category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    gross_excl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    net_excl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    vat_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_incl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    project_quote_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_quote_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_quote_lines_project_quotes_project_quote_id",
                        column: x => x.project_quote_id,
                        principalSchema: "leads",
                        principalTable: "project_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_quote_lines_project_quote_id",
                schema: "leads",
                table: "project_quote_lines",
                column: "project_quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_quotes_opportunity_id",
                schema: "leads",
                table: "project_quotes",
                column: "opportunity_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_quote_lines",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "project_quotes",
                schema: "leads");
        }
    }
}
