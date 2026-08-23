using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateTableName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_permission_attribution_app_users_UserId",
                schema: "administration",
                table: "permission_attribution");

            migrationBuilder.DropPrimaryKey(
                name: "PK_permission_attribution",
                schema: "administration",
                table: "permission_attribution");

            migrationBuilder.DropIndex(
                name: "IX_permission_attribution_UserId",
                schema: "administration",
                table: "permission_attribution");

            migrationBuilder.RenameTable(
                name: "permission_attribution",
                schema: "administration",
                newName: "permission_attributions",
                newSchema: "administration");

            migrationBuilder.RenameColumn(
                name: "UpdateAt",
                schema: "administration",
                table: "permission_attributions",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "CreateAt",
                schema: "administration",
                table: "permission_attributions",
                newName: "CreatedAt");

            migrationBuilder.AlterColumn<string>(
                name: "ScopeType",
                schema: "administration",
                table: "permission_attributions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PermissionCode",
                schema: "administration",
                table: "permission_attributions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddPrimaryKey(
                name: "PK_permission_attributions",
                schema: "administration",
                table: "permission_attributions",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_permission_attributions_TenantId_UserId",
                schema: "administration",
                table: "permission_attributions",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_permission_attributions_UserId_PermissionCode_ScopeId",
                schema: "administration",
                table: "permission_attributions",
                columns: new[] { "UserId", "PermissionCode", "ScopeId" },
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_permission_attributions_app_users_UserId",
                schema: "administration",
                table: "permission_attributions",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_permission_attributions_app_users_UserId",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_permission_attributions",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropIndex(
                name: "IX_permission_attributions_TenantId_UserId",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropIndex(
                name: "IX_permission_attributions_UserId_PermissionCode_ScopeId",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.RenameTable(
                name: "permission_attributions",
                schema: "administration",
                newName: "permission_attribution",
                newSchema: "administration");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "administration",
                table: "permission_attribution",
                newName: "UpdateAt");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "administration",
                table: "permission_attribution",
                newName: "CreateAt");

            migrationBuilder.AlterColumn<string>(
                name: "ScopeType",
                schema: "administration",
                table: "permission_attribution",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PermissionCode",
                schema: "administration",
                table: "permission_attribution",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddPrimaryKey(
                name: "PK_permission_attribution",
                schema: "administration",
                table: "permission_attribution",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_permission_attribution_UserId",
                schema: "administration",
                table: "permission_attribution",
                column: "UserId",
                unique: true,
                filter: "\"IsActive\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_permission_attribution_app_users_UserId",
                schema: "administration",
                table: "permission_attribution",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
