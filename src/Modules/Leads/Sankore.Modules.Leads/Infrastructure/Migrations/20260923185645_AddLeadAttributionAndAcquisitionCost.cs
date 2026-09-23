using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAttributionAndAcquisitionCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "acquisition_cost",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acquisition_currency",
                schema: "leads",
                table: "leads",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "landing_page",
                schema: "leads",
                table: "leads",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "referrer",
                schema: "leads",
                table: "leads",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "social_interaction_id",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "social_publication_id",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_campaign",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_content",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_medium",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_source",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "utm_term",
                schema: "leads",
                table: "leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acquisition_cost",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "acquisition_currency",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "external_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "landing_page",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "referrer",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "social_interaction_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "social_publication_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "utm_campaign",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "utm_content",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "utm_medium",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "utm_source",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "utm_term",
                schema: "leads",
                table: "leads");
        }
    }
}
