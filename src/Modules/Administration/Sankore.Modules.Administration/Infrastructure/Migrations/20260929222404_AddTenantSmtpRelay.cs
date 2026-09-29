using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSmtpRelay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // credential_vault_path is dropped on purpose. It held a path supplied by the caller
            // that nothing ever wrote to and nothing ever read: the credential now lives in the
            // secrets vault under a key derived from (tenant, provider), so there is no path to
            // keep in step. Dropping it removes a column that could only ever mislead.

            migrationBuilder.DropColumn(
                name: "credential_vault_path",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.AddColumn<bool>(
                name: "has_credential",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "smtp_host",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "smtp_port",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "smtp_use_ssl",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "smtp_use_start_tls",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "smtp_username",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "has_credential",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropColumn(
                name: "smtp_host",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropColumn(
                name: "smtp_port",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropColumn(
                name: "smtp_use_ssl",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropColumn(
                name: "smtp_use_start_tls",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropColumn(
                name: "smtp_username",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.AddColumn<string>(
                name: "credential_vault_path",
                schema: "administration",
                table: "tenant_notification_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }
    }
}
