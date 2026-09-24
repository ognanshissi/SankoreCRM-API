using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultAgencyAndDispatchingRuleToLeadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "default_agency_id",
                schema: "leads",
                table: "lead_source_configs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "default_dispatching_rule_id",
                schema: "leads",
                table: "lead_source_configs",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_agency_id",
                schema: "leads",
                table: "lead_source_configs");

            migrationBuilder.DropColumn(
                name: "default_dispatching_rule_id",
                schema: "leads",
                table: "lead_source_configs");
        }
    }
}
