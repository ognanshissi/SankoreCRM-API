using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencySelfReferenceFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                column: "ParentAgencyId");

            migrationBuilder.AddForeignKey(
                name: "FK_agencies_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                column: "ParentAgencyId",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agencies_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropIndex(
                name: "IX_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies");
        }
    }
}
