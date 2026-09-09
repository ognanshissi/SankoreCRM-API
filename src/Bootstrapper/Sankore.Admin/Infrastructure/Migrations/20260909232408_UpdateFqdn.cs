using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateFqdn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_Fqdn",
                table: "Tenants");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Fqdn",
                table: "Tenants",
                column: "Fqdn",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_Fqdn",
                table: "Tenants");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Fqdn",
                table: "Tenants",
                column: "Fqdn");
        }
    }
}
