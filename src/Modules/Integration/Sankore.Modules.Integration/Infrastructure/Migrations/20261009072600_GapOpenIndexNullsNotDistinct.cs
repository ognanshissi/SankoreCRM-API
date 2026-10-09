using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GapOpenIndexNullsNotDistinct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_integration_reconciliation_gap_open",
                schema: "integration",
                table: "integration_reconciliation_gap");

            migrationBuilder.CreateIndex(
                name: "ux_integration_reconciliation_gap_open",
                schema: "integration",
                table: "integration_reconciliation_gap",
                columns: new[] { "tenant_id", "connection_id", "gap_type", "crm_id", "external_id" },
                unique: true,
                filter: "resolution = 'Open'")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_integration_reconciliation_gap_open",
                schema: "integration",
                table: "integration_reconciliation_gap");

            migrationBuilder.CreateIndex(
                name: "ux_integration_reconciliation_gap_open",
                schema: "integration",
                table: "integration_reconciliation_gap",
                columns: new[] { "tenant_id", "connection_id", "gap_type", "crm_id", "external_id" },
                unique: true,
                filter: "resolution = 'Open'");
        }
    }
}
