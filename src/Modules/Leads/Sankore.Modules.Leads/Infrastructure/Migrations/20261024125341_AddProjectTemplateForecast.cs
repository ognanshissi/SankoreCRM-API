using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTemplateForecast : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "tracks_forecast",
                schema: "leads",
                table: "project_templates",
                type: "boolean",
                nullable: false,
                // Existing templates keep showing what they showed: probability and close date.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tracks_forecast",
                schema: "leads",
                table: "project_templates");
        }
    }
}
