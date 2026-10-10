using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrincipalIdentityAndPepDeclaration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seven nullable columns and NO backfill, deliberately.
            //
            // `pep_declared_at` null means "nobody has declared a PEP status", which is exactly
            // true of every existing row: PepStatus.NotExposed was the owned type's default, not
            // an answer anybody gave (see Lead.PepDeclaredAt). Backfilling it with a timestamp
            // would manufacture a declaration, and the completeness check would then report every
            // pre-existing lead as having answered a question it was never asked.
            //
            // The five principal_* columns are new fields nobody has collected yet, so null is the
            // only honest value. `principal_address_enc` holds ciphertext and therefore carries no
            // length cap and no index — see LeadConfiguration.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pep_declared_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_address_enc",
                schema: "leads",
                table: "leads",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_city",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_country_code",
                schema: "leads",
                table: "leads",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_postal_code",
                schema: "leads",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_usage_name",
                schema: "leads",
                table: "leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pep_declared_at",
                schema: "leads",
                table: "lead_household_members",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pep_declared_at",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_address_enc",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_city",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_country_code",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_postal_code",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_usage_name",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "pep_declared_at",
                schema: "leads",
                table: "lead_household_members");
        }
    }
}
