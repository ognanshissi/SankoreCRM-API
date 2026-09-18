using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadIdentityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_reference",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "national_id",
                schema: "leads",
                table: "leads",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_customer_reference",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "customer_reference" },
                filter: "customer_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_national_id",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "national_id" },
                filter: "national_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_customer_reference",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_national_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "customer_reference",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "national_id",
                schema: "leads",
                table: "leads");
        }
    }
}
