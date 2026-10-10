using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAnonymizationFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "anonymized_at",
                schema: "leads",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_anonymized",
                schema: "leads",
                table: "leads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ix_leads_retention_candidates",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "updated_at" },
                filter: "NOT is_anonymized");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_retention_candidates",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "anonymized_at",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "is_anonymized",
                schema: "leads",
                table: "leads");
        }
    }
}
