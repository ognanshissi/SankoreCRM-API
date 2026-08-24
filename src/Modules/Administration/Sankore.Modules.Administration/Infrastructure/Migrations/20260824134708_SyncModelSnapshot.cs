using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncModelSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_AgencyId_RequiredForStandard",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_User_System_NoAgency",
                schema: "administration",
                table: "app_users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_AgencyId_RequiredForStandard",
                schema: "administration",
                table: "app_users",
                sql: "(\"AccountType\" != 'Standard') OR (\"AgencyId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_System_NoAgency",
                schema: "administration",
                table: "app_users",
                sql: "(\"AccountType\" != 'System') OR (\"AgencyId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_User_AgencyId_RequiredForStandard",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_User_System_NoAgency",
                schema: "administration",
                table: "app_users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_AgencyId_RequiredForStandard",
                schema: "administration",
                table: "app_users",
                sql: "(\"AccountType\" != 0) OR (\"AgencyId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_User_System_NoAgency",
                schema: "administration",
                table: "app_users",
                sql: "(\"AccountType\" != 1) OR (\"AgencyId\" IS NULL)");
        }
    }
}
