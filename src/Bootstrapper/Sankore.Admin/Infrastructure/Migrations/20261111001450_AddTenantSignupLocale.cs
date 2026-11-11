using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSignupLocale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Locale",
                table: "TenantSignups",
                type: "character varying(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "fr");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Locale",
                table: "TenantSignups");
        }
    }
}
