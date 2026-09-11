using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicationUrl",
                table: "Tenants");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AlterColumn<string>(
                name: "Fqdn",
                table: "Tenants",
                type: "citext",
                maxLength: 253,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "TenantDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fqdn = table.Column<string>(type: "citext", maxLength: 253, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDomains", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_fqdn",
                table: "TenantDomains",
                column: "Fqdn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_tenant_id",
                table: "TenantDomains",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_domains_tenant_primary",
                table: "TenantDomains",
                columns: new[] { "TenantId", "IsPrimary" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantDomains");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.AlterColumn<string>(
                name: "Fqdn",
                table: "Tenants",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "citext",
                oldMaxLength: 253);

            migrationBuilder.AddColumn<string>(
                name: "ApplicationUrl",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
