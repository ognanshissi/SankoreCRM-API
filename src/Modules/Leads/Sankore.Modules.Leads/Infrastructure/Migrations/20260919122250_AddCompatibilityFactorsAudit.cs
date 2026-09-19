using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompatibilityFactorsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "compatibility_factors_json",
                schema: "leads",
                table: "lead_assignments",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<double>(
                name: "weights_agency",
                schema: "leads",
                table: "dispatching_rules",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compatibility_factors_json",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropColumn(
                name: "weights_agency",
                schema: "leads",
                table: "dispatching_rules");
        }
    }
}
