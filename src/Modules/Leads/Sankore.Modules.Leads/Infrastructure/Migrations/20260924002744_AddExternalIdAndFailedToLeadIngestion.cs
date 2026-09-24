using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalIdAndFailedToLeadIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                schema: "leads",
                table: "leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                schema: "leads",
                table: "lead_ingestions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_ingestion_idempotency",
                schema: "leads",
                table: "lead_ingestions",
                columns: new[] { "tenant_id", "source_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ingestion_idempotency",
                schema: "leads",
                table: "lead_ingestions");

            migrationBuilder.DropColumn(
                name: "is_test",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "external_id",
                schema: "leads",
                table: "lead_ingestions");
        }
    }
}
