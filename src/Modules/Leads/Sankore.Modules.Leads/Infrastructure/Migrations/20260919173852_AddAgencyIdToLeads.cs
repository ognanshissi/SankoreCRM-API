using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencyIdToLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // agency_id was added to the Lead entity after the initial migration
            // and was never captured in an Up() script — this backfills the column.
            migrationBuilder.AddColumn<Guid>(
                name: "agency_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agency_id",
                schema: "leads",
                table: "leads");
        }
    }
}
