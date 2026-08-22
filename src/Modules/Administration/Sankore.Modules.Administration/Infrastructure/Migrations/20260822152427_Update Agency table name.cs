using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAgencytablename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agencies_territories_TerritoryId",
                schema: "administration",
                table: "Agencies");

            migrationBuilder.DropForeignKey(
                name: "FK_app_users_Agencies_AgencyId",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Agencies",
                schema: "administration",
                table: "Agencies");

            migrationBuilder.RenameTable(
                name: "Agencies",
                schema: "administration",
                newName: "agencies",
                newSchema: "administration");

            migrationBuilder.RenameIndex(
                name: "IX_Agencies_TerritoryId",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TerritoryId");

            migrationBuilder.RenameIndex(
                name: "IX_Agencies_TenantId_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TenantId_ParentAgencyId");

            migrationBuilder.RenameIndex(
                name: "IX_Agencies_TenantId_Code",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TenantId_Code");

            migrationBuilder.AddPrimaryKey(
                name: "PK_agencies",
                schema: "administration",
                table: "agencies",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_agencies_territories_TerritoryId",
                schema: "administration",
                table: "agencies",
                column: "TerritoryId",
                principalSchema: "administration",
                principalTable: "territories",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_app_users_agencies_AgencyId",
                schema: "administration",
                table: "app_users",
                column: "AgencyId",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agencies_territories_TerritoryId",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropForeignKey(
                name: "FK_app_users_agencies_AgencyId",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_agencies",
                schema: "administration",
                table: "agencies");

            migrationBuilder.RenameTable(
                name: "agencies",
                schema: "administration",
                newName: "Agencies",
                newSchema: "administration");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TerritoryId",
                schema: "administration",
                table: "Agencies",
                newName: "IX_Agencies_TerritoryId");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TenantId_ParentAgencyId",
                schema: "administration",
                table: "Agencies",
                newName: "IX_Agencies_TenantId_ParentAgencyId");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TenantId_Code",
                schema: "administration",
                table: "Agencies",
                newName: "IX_Agencies_TenantId_Code");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Agencies",
                schema: "administration",
                table: "Agencies",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Agencies_territories_TerritoryId",
                schema: "administration",
                table: "Agencies",
                column: "TerritoryId",
                principalSchema: "administration",
                principalTable: "territories",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_app_users_Agencies_AgencyId",
                schema: "administration",
                table: "app_users",
                column: "AgencyId",
                principalSchema: "administration",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
