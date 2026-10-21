using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentNotificationSound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // true for every existing agent: the bell sounds unless somebody turns it off — the
            // domain's own default. EF scaffolds false for a new bool column, which would have
            // silenced everybody on the day of the deploy.
            migrationBuilder.AddColumn<bool>(
                name: "notification_sound",
                schema: "administration",
                table: "agent_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "notification_sound",
                schema: "administration",
                table: "agent_settings");
        }
    }
}
