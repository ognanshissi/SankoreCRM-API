using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadCompanyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "activity_sector_code",
                schema: "leads",
                table: "leads",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "company_size",
                schema: "leads",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_job_title",
                schema: "leads",
                table: "leads",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_annual_purchases",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "estimated_annual_purchases_currency",
                schema: "leads",
                table: "leads",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "activity_sector_code",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "company_size",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "contact_job_title",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "estimated_annual_purchases",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "estimated_annual_purchases_currency",
                schema: "leads",
                table: "leads");
        }
    }
}
