using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedCalendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cal_calendar",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_calendar_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    is_read_only = table.Column<bool>(type: "boolean", nullable: false),
                    is_subscription = table.Column<bool>(type: "boolean", nullable: false),
                    is_selected = table.Column<bool>(type: "boolean", nullable: false),
                    sync_cursor = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    event_count = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_calendar", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cal_event",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_event_id = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_all_day = table.Column<bool>(type: "boolean", nullable: false),
                    show_as = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title_enc = table.Column<string>(type: "text", nullable: true),
                    location_enc = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cal_calendar_tenant_id_user_id",
                schema: "integration",
                table: "cal_calendar",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_cal_calendar_account_external",
                schema: "integration",
                table: "cal_calendar",
                columns: new[] { "tenant_id", "account_id", "external_calendar_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cal_event_tenant_id_user_id_start_at",
                schema: "integration",
                table: "cal_event",
                columns: new[] { "tenant_id", "user_id", "start_at" });

            migrationBuilder.CreateIndex(
                name: "ux_cal_event_calendar_external",
                schema: "integration",
                table: "cal_event",
                columns: new[] { "tenant_id", "calendar_id", "external_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_calendar",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cal_event",
                schema: "integration");
        }
    }
}
