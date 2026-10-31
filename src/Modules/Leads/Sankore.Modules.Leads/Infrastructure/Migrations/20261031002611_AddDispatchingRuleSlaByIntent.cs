using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchingRuleSlaByIntent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "first_contact_sla_cold",
                schema: "leads",
                table: "dispatching_rules",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "first_contact_sla_hot",
                schema: "leads",
                table: "dispatching_rules",
                type: "interval",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "first_contact_sla_warm",
                schema: "leads",
                table: "dispatching_rules",
                type: "interval",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "first_contact_sla_cold",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.DropColumn(
                name: "first_contact_sla_hot",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.DropColumn(
                name: "first_contact_sla_warm",
                schema: "leads",
                table: "dispatching_rules");
        }
    }
}
