using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <summary>
    /// user_profile stored its identifier in a column named « additional_email », which pushed the
    /// real additional e-mail into « additional_email1 » (found 2026-11-10). Written BY HAND: EF
    /// generated a drop of the e-mail column and a new, empty « id » primary key. Two renames move
    /// no data and keep the primary key constraint.
    /// </summary>
    public partial class RenameUserProfileColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "additional_email", schema: "administration", table: "user_profile", newName: "id");
            migrationBuilder.RenameColumn(
                name: "additional_email1", schema: "administration", table: "user_profile", newName: "additional_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "additional_email", schema: "administration", table: "user_profile", newName: "additional_email1");
            migrationBuilder.RenameColumn(
                name: "id", schema: "administration", table: "user_profile", newName: "additional_email");
        }
    }
}
