using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectedMailbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "include_mail",
                schema: "integration",
                table: "cal_oauth_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_mail_enabled",
                schema: "integration",
                table: "cal_connected_account",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "mail_enabled_at",
                schema: "integration",
                table: "cal_connected_account",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cal_mail_server",
                schema: "integration",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imap_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    imap_port = table.Column<int>(type: "integer", nullable: false),
                    imap_security = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    smtp_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    smtp_port = table.Column<int>(type: "integer", nullable: false),
                    smtp_security = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_server", x => x.account_id);
                    table.ForeignKey(
                        name: "fk_cal_mail_server_cal_connected_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "integration",
                        principalTable: "cal_connected_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_mail_server",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "include_mail",
                schema: "integration",
                table: "cal_oauth_state");

            migrationBuilder.DropColumn(
                name: "is_mail_enabled",
                schema: "integration",
                table: "cal_connected_account");

            migrationBuilder.DropColumn(
                name: "mail_enabled_at",
                schema: "integration",
                table: "cal_connected_account");
        }
    }
}
