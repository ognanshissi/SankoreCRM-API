using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserInvitationAndImpersonation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "impersonator_user_id",
                schema: "administration",
                table: "user_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by_user_id",
                schema: "administration",
                table: "app_users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "invitation_sent_at",
                schema: "administration",
                table: "app_users",
                type: "timestamp with time zone",
                nullable: true);

            // Matricules des comptes existants, avant l'index unique : AGT-0001… par tenant, dans
            // l'ordre de création, à la suite du plus grand déjà attribué. Les comptes système et
            // super-utilisateurs n'en reçoivent pas. Un matricule saisi à la main en double
            // ferait échouer l'index — ce serait un doublon réel à trancher, pas à masquer.
            migrationBuilder.Sql("""
                WITH base AS (
                    SELECT tenant_id,
                           COALESCE(MAX(substring(employee_number FROM 5)::int)
                               FILTER (WHERE employee_number ~ '^AGT-[0-9]+$'), 0) AS mx
                    FROM administration.app_users
                    GROUP BY tenant_id),
                numbered AS (
                    SELECT u.id, u.tenant_id,
                           row_number() OVER (PARTITION BY u.tenant_id ORDER BY u.created_at NULLS LAST, u.id) AS rn
                    FROM administration.app_users u
                    WHERE u.employee_number IS NULL
                      AND u.is_super_user = false
                      AND u.account_type = 'Standard')
                UPDATE administration.app_users u
                SET employee_number = 'AGT-' || lpad((b.mx + n.rn)::text, 4, '0')
                FROM numbered n JOIN base b ON b.tenant_id = n.tenant_id
                WHERE u.id = n.id;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_app_users_tenant_id_employee_number",
                schema: "administration",
                table: "app_users",
                columns: new[] { "tenant_id", "employee_number" },
                unique: true,
                filter: "employee_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_app_users_tenant_id_employee_number",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropColumn(
                name: "impersonator_user_id",
                schema: "administration",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropColumn(
                name: "invitation_sent_at",
                schema: "administration",
                table: "app_users");
        }
    }
}
