using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "outcome",
                schema: "administration",
                table: "user_login_locations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Succeeded");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reported_at",
                schema: "administration",
                table: "user_login_locations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                schema: "administration",
                table: "user_login_locations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                schema: "administration",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    browser = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    client_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_sessions_app_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "administration",
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "administration",
                table: "refresh_tokens",
                column: "session_id",
                filter: "session_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_tenant_id_user_id_revoked_at",
                schema: "administration",
                table: "user_sessions",
                columns: new[] { "tenant_id", "user_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                schema: "administration",
                table: "user_sessions",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "administration");

            migrationBuilder.DropIndex(
                name: "ix_refresh_tokens_session_id",
                schema: "administration",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "outcome",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "reported_at",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "administration",
                table: "refresh_tokens");
        }
    }
}
