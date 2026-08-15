using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Users.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SmallChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserClaims");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserLogins");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Agencies_AgencyId",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_user_login_locations_AspNetUsers_UserId",
                schema: "identity",
                table: "user_login_locations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetUsers",
                schema: "identity",
                table: "AspNetUsers");

            migrationBuilder.RenameTable(
                name: "AspNetUsers",
                schema: "identity",
                newName: "app_users",
                newSchema: "identity");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUsers_TenantId_AgencyId",
                schema: "identity",
                table: "app_users",
                newName: "IX_app_users_TenantId_AgencyId");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUsers_AgencyId",
                schema: "identity",
                table: "app_users",
                newName: "IX_app_users_AgencyId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_users",
                schema: "identity",
                table: "app_users",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_app_users_Agencies_AgencyId",
                schema: "identity",
                table: "app_users",
                column: "AgencyId",
                principalSchema: "identity",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserClaims_app_users_UserId",
                schema: "identity",
                table: "AspNetUserClaims",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserLogins_app_users_UserId",
                schema: "identity",
                table: "AspNetUserLogins",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserRoles_app_users_UserId",
                schema: "identity",
                table: "AspNetUserRoles",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserTokens_app_users_UserId",
                schema: "identity",
                table: "AspNetUserTokens",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_login_locations_app_users_UserId",
                schema: "identity",
                table: "user_login_locations",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_app_users_Agencies_AgencyId",
                schema: "identity",
                table: "app_users");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserClaims_app_users_UserId",
                schema: "identity",
                table: "AspNetUserClaims");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserLogins_app_users_UserId",
                schema: "identity",
                table: "AspNetUserLogins");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserRoles_app_users_UserId",
                schema: "identity",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserTokens_app_users_UserId",
                schema: "identity",
                table: "AspNetUserTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_user_login_locations_app_users_UserId",
                schema: "identity",
                table: "user_login_locations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_users",
                schema: "identity",
                table: "app_users");

            migrationBuilder.RenameTable(
                name: "app_users",
                schema: "identity",
                newName: "AspNetUsers",
                newSchema: "identity");

            migrationBuilder.RenameIndex(
                name: "IX_app_users_TenantId_AgencyId",
                schema: "identity",
                table: "AspNetUsers",
                newName: "IX_AspNetUsers_TenantId_AgencyId");

            migrationBuilder.RenameIndex(
                name: "IX_app_users_AgencyId",
                schema: "identity",
                table: "AspNetUsers",
                newName: "IX_AspNetUsers_AgencyId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetUsers",
                schema: "identity",
                table: "AspNetUsers",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserClaims",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserLogins",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserRoles",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Agencies_AgencyId",
                schema: "identity",
                table: "AspNetUsers",
                column: "AgencyId",
                principalSchema: "identity",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                schema: "identity",
                table: "AspNetUserTokens",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_login_locations_AspNetUsers_UserId",
                schema: "identity",
                table: "user_login_locations",
                column: "UserId",
                principalSchema: "identity",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
