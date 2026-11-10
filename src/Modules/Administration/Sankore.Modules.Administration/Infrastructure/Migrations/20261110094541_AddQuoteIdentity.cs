using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Quote lines, lot 2 (2026-11-10). DEV and 30 days for the institutions already there;
            // deliberately not model defaults (see CompanyInfoConfiguration). The counters' table
            // and key are written by hand in QuoteIssuerReader.TakeAsync.
            migrationBuilder.AddColumn<string>(
                name: "head_office_address",
                schema: "administration",
                table: "company_info",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legal_email",
                schema: "administration",
                table: "company_info",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legal_form",
                schema: "administration",
                table: "company_info",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legal_name",
                schema: "administration",
                table: "company_info",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "legal_phone",
                schema: "administration",
                table: "company_info",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quote_footer",
                schema: "administration",
                table: "company_info",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quote_payment_terms",
                schema: "administration",
                table: "company_info",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quote_prefix",
                schema: "administration",
                table: "company_info",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "DEV");

            migrationBuilder.AddColumn<int>(
                name: "quote_validity_days",
                schema: "administration",
                table: "company_info",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "rccm",
                schema: "administration",
                table: "company_info",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_regime",
                schema: "administration",
                table: "company_info",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "taxpayer_number",
                schema: "administration",
                table: "company_info",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website",
                schema: "administration",
                table: "company_info",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "quote_number_counters",
                schema: "administration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_number_counters", x => new { x.tenant_id, x.year });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quote_number_counters",
                schema: "administration");

            migrationBuilder.DropColumn(
                name: "head_office_address",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "legal_email",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "legal_form",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "legal_name",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "legal_phone",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "quote_footer",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "quote_payment_terms",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "quote_prefix",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "quote_validity_days",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "rccm",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "tax_regime",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "taxpayer_number",
                schema: "administration",
                table: "company_info");

            migrationBuilder.DropColumn(
                name: "website",
                schema: "administration",
                table: "company_info");
        }
    }
}
