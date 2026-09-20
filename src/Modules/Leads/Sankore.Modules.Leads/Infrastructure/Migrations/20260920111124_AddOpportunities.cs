using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpportunities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "opportunities",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    customer_entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    customer_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    product = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    estimated_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    estimated_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    stage = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    probability = table.Column<double>(type: "double precision", nullable: false),
                    expected_close_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_opportunities", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_opportunities_tenant_id_customer_entity_id",
                schema: "leads",
                table: "opportunities",
                columns: new[] { "tenant_id", "customer_entity_id" },
                filter: "customer_entity_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_opportunities_tenant_id_lead_id",
                schema: "leads",
                table: "opportunities",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_opportunities_tenant_id_stage",
                schema: "leads",
                table: "opportunities",
                columns: new[] { "tenant_id", "stage" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "opportunities",
                schema: "leads");
        }
    }
}
