using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDispatchQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "awaiting_dispatch_since",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_awaiting_dispatch_since",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "awaiting_dispatch_since" },
                filter: "awaiting_dispatch_since IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_awaiting_dispatch_since",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "awaiting_dispatch_since",
                schema: "leads",
                table: "leads");
        }
    }
}
