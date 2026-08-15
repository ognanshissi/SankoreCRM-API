using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgentCollectedLeadId",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CurrentAssignedId",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentCollectedLeadId",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "CurrentAssignedId",
                schema: "leads",
                table: "leads");
        }
    }
}
