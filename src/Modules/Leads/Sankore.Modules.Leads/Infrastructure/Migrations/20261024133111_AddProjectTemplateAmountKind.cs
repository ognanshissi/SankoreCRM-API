using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplateAmountKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "amount_kind",
                schema: "leads",
                table: "project_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Generic");

            migrationBuilder.AddColumn<bool>(
                name: "critical_close_date",
                schema: "leads",
                table: "project_templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // A family's template already says what its amounts are: a loan family disburses, an
            // insurance family collects premiums, a savings family collects deposits.
            migrationBuilder.Sql("""
                UPDATE leads.project_templates SET amount_kind = CASE
                    WHEN category IN ('Loan', 'GroupCredit', 'Agriculture') THEN 'Loan'
                    WHEN category IN ('Insurance', 'HealthInsurance', 'ForecastInsurance') THEN 'Premium'
                    WHEN category IN ('Savings', 'Tontine') THEN 'Deposit'
                    ELSE 'Generic' END
                WHERE product_code IS NULL AND category IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "amount_kind",
                schema: "leads",
                table: "project_templates");

            migrationBuilder.DropColumn(
                name: "critical_close_date",
                schema: "leads",
                table: "project_templates");
        }
    }
}
