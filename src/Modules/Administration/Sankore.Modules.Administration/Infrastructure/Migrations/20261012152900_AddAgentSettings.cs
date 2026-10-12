using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_settings",
                schema: "administration",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    availability = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Available"),
                    working_hours = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    max_active_leads = table.Column<int>(type: "integer", nullable: true),
                    capacity_ceiling = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    notification_preferences = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    absence_from = table.Column<DateOnly>(type: "date", nullable: true),
                    absence_to = table.Column<DateOnly>(type: "date", nullable: true),
                    replacement_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transfer_tasks_to_replacement = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_settings", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_settings_tenant_id",
                schema: "administration",
                table: "agent_settings",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_settings",
                schema: "administration");
        }
    }
}
