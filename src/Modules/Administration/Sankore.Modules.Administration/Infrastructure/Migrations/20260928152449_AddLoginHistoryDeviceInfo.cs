using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginHistoryDeviceInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "browser",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "browser_version",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_kind",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ip_address",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "user_agent",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_login_locations_tenant_id_ip_address",
                schema: "administration",
                table: "user_login_locations",
                columns: new[] { "tenant_id", "ip_address" },
                filter: "ip_address IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_login_locations_tenant_id_user_id_occured_at",
                schema: "administration",
                table: "user_login_locations",
                columns: new[] { "tenant_id", "user_id", "occured_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_login_locations_tenant_id_ip_address",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropIndex(
                name: "ix_user_login_locations_tenant_id_user_id_occured_at",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "browser",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "browser_version",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "client_kind",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "ip_address",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "platform",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "user_agent",
                schema: "administration",
                table: "user_login_locations");
        }
    }
}
