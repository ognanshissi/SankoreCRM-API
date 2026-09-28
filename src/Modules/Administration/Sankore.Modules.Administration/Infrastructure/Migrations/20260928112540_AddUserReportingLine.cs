using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserReportingLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reports_to_user_id",
                schema: "administration",
                table: "app_users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_users_tenant_id_reports_to_user_id",
                schema: "administration",
                table: "app_users",
                columns: new[] { "tenant_id", "reports_to_user_id" },
                filter: "reports_to_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_app_users_tenant_id_reports_to_user_id",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropColumn(
                name: "reports_to_user_id",
                schema: "administration",
                table: "app_users");
        }
    }
}
