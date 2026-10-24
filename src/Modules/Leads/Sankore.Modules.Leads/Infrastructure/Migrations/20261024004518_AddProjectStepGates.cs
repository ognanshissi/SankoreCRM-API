using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectStepGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_approval",
                schema: "leads",
                table: "project_template_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "opens_client_file",
                schema: "leads",
                table: "project_template_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Lot 3: a converted lead's projects follow it onto the client record. The conversion
            // does it from now on; this brings the projects of leads converted before.
            migrationBuilder.Sql("""
                UPDATE leads.opportunities o
                   SET customer_entity_id = l.converted_to_customer_id,
                       customer_entity_type = 'Customer'
                  FROM leads.leads l
                 WHERE o.lead_id = l.id
                   AND o.customer_entity_id IS NULL
                   AND l.converted_to_customer_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_approval",
                schema: "leads",
                table: "project_template_steps");

            migrationBuilder.DropColumn(
                name: "opens_client_file",
                schema: "leads",
                table: "project_template_steps");
        }
    }
}
