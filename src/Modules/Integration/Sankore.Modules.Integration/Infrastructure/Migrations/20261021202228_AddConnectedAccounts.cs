using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No `xmin` column on cal_connected_account although the model maps one: it is
            // PostgreSQL's system column, present on every table, and creating it fails with
            // « column name "xmin" conflicts with a system column name » (see AddRelayAgentRegistry).
            migrationBuilder.CreateTable(
                name: "cal_connected_account",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    granted_scopes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_connected_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cal_oauth_state",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    return_origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_oauth_state", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_cal_connected_account_user_external",
                schema: "integration",
                table: "cal_connected_account",
                columns: new[] { "tenant_id", "user_id", "provider", "external_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_cal_oauth_state_hash",
                schema: "integration",
                table: "cal_oauth_state",
                column: "state_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_connected_account",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cal_oauth_state",
                schema: "integration");
        }
    }
}
