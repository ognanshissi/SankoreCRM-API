using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "company_info",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    logo_url = table.Column<string>(type: "text", nullable: false),
                    is_maintenance = table.Column<bool>(type: "boolean", nullable: false),
                    primary_color = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    secondary_color = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    default_language = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_company_info", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_company_info_tenant_id",
                schema: "administration",
                table: "company_info",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_company_info_tenant_id_id",
                schema: "administration",
                table: "company_info",
                columns: new[] { "tenant_id", "id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "company_info",
                schema: "administration");
        }
    }
}
