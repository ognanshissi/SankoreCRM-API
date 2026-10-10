using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadHouseholdAndPep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_pep_exposure",
                schema: "leads",
                table: "leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_country",
                schema: "leads",
                table: "leads",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "principal_pep_end_date",
                schema: "leads",
                table: "leads",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_function",
                schema: "leads",
                table: "leads",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "principal_pep_is_exposed",
                schema: "leads",
                table: "leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_link",
                schema: "leads",
                table: "leads",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "principal_pep_override_reason",
                schema: "leads",
                table: "leads",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "lead_household_members",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    spouse_union_type = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    uses_principal_address = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    usage_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    birth_date_enc = table.Column<string>(type: "text", nullable: true),
                    gender = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    address_enc = table.Column<string>(type: "text", nullable: true),
                    postal_code = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    country_code = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    fr_nir_enc = table.Column<string>(type: "text", nullable: true),
                    fr_organism_code = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    fr_attached_to_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pep_is_exposed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    pep_function = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    pep_country = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    pep_end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    pep_link = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    pep_override_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removal_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_household_members", x => x.id);
                    table.CheckConstraint("ck_lhm_union", "(type = 'Spouse') = (spouse_union_type IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_lead_household_members_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_has_pep_exposure",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "has_pep_exposure" },
                filter: "has_pep_exposure");

            migrationBuilder.CreateIndex(
                name: "ix_lead_household_members_tenant_id_lead_id",
                schema: "leads",
                table: "lead_household_members",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "uq_lead_one_spouse",
                schema: "leads",
                table: "lead_household_members",
                column: "lead_id",
                unique: true,
                filter: "type = 'Spouse' AND removed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_household_members",
                schema: "leads");

            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_has_pep_exposure",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "has_pep_exposure",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_country",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_end_date",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_function",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_is_exposed",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_link",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "principal_pep_override_reason",
                schema: "leads",
                table: "leads");
        }
    }
}
