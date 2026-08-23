using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Api.Infrastructure.Audit.Migrations
{
    /// <inheritdoc />
    public partial class AddResourceEnrichment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResourceId",
                schema: "audit",
                table: "entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceType",
                schema: "audit",
                table: "entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_entries_TenantId_ResourceType_ResourceId",
                schema: "audit",
                table: "entries",
                columns: new[] { "TenantId", "ResourceType", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_entries_TenantId_ResourceType_ResourceId",
                schema: "audit",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "ResourceId",
                schema: "audit",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "ResourceType",
                schema: "audit",
                table: "entries");
        }
    }
}
