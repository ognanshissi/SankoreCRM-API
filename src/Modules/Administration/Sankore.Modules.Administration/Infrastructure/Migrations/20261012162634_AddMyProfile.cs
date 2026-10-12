using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMyProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "amount_format",
                schema: "administration",
                table: "user_profile",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "SpaceGrouped");

            migrationBuilder.AddColumn<string>(
                name: "signature",
                schema: "administration",
                table: "user_profile",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "administration",
                table: "user_profile",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Africa/Abidjan");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "administration",
                table: "user_profile",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "whats_app_number",
                schema: "administration",
                table: "user_profile",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "administration",
                table: "app_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "employee_number",
                schema: "administration",
                table: "app_users",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Arrivée des comptes existants : la plus ancienne trace connue (premier rôle attribué,
            // première connexion). Sans trace, la date reste inconnue plutôt que de prendre la date
            // de cette migration pour une date d'arrivée.
            migrationBuilder.Sql("""
                UPDATE administration.app_users u
                SET created_at = t.first_seen
                FROM (
                    SELECT user_id, MIN(at) AS first_seen FROM (
                        SELECT user_id, assigned_at AS at FROM administration.user_roles
                        UNION ALL
                        SELECT user_id, occured_at AS at FROM administration.user_login_locations
                    ) traces
                    GROUP BY user_id
                ) t
                WHERE u.id = t.user_id AND u.created_at IS NULL;
                """);

            migrationBuilder.CreateTable(
                name: "user_avatars",
                schema: "administration",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false),
                    content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_avatars", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_avatars_tenant_id",
                schema: "administration",
                table: "user_avatars",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_avatars",
                schema: "administration");

            migrationBuilder.DropColumn(
                name: "amount_format",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropColumn(
                name: "signature",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropColumn(
                name: "whats_app_number",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropColumn(
                name: "employee_number",
                schema: "administration",
                table: "app_users");
        }
    }
}
