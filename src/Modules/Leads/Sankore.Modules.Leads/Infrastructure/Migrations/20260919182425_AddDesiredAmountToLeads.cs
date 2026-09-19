using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDesiredAmountToLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill: desired_amount is the Amount half of the Money owned type.
            // desired_currency was migrated in AddLeadCoreV2 but this column was missed.
            migrationBuilder.AddColumn<decimal>(
                name: "desired_amount",
                schema: "leads",
                table: "leads",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "desired_amount",
                schema: "leads",
                table: "leads");
        }
    }
}
