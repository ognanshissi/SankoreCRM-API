using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentCalendarCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_appointment_target",
                schema: "integration",
                table: "cal_calendar",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "cal_appointment_copy",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_event_id = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_appointment_copy", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_cal_calendar_appointment_target",
                schema: "integration",
                table: "cal_calendar",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "is_appointment_target");

            migrationBuilder.CreateIndex(
                name: "ix_cal_appointment_copy_tenant_id_calendar_id_external_event_id",
                schema: "integration",
                table: "cal_appointment_copy",
                columns: new[] { "tenant_id", "calendar_id", "external_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cal_appointment_copy_tenant_id_status",
                schema: "integration",
                table: "cal_appointment_copy",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_cal_appointment_copy_appointment_user",
                schema: "integration",
                table: "cal_appointment_copy",
                columns: new[] { "tenant_id", "appointment_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_appointment_copy",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ux_cal_calendar_appointment_target",
                schema: "integration",
                table: "cal_calendar");

            migrationBuilder.DropColumn(
                name: "is_appointment_target",
                schema: "integration",
                table: "cal_calendar");
        }
    }
}
