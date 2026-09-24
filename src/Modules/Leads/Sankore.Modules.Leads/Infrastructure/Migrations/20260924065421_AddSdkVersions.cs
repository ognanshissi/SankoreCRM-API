using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSdkVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sdk_versions",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    major = table.Column<int>(type: "integer", nullable: false),
                    sri_hash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sdk_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sdk_major_current",
                schema: "leads",
                table: "sdk_versions",
                columns: new[] { "major", "is_current" },
                filter: "is_current = true");

            migrationBuilder.CreateIndex(
                name: "ux_sdk_version",
                schema: "leads",
                table: "sdk_versions",
                column: "version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sdk_versions",
                schema: "leads");
        }
    }
}
