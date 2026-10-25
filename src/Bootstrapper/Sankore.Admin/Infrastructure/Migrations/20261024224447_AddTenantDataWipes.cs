using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantDataWipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "tenant_data_wipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_data_wipes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_data_wipes_Status_RequestedAt",
                table: "tenant_data_wipes",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_data_wipes_TenantId_RequestedAt",
                table: "tenant_data_wipes",
                columns: new[] { "TenantId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_data_wipes_in_flight",
                table: "tenant_data_wipes",
                column: "TenantId",
                unique: true,
                filter: "\"Status\" IN ('Pending', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_data_wipes");

            migrationBuilder.DropColumn(
                name: "IsTest",
                table: "Tenants");
        }
    }
}
