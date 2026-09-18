using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadMergeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_merges",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merged_by = table.Column<Guid>(type: "uuid", nullable: false),
                    merged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    overridden_fields = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_merges", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_merges_tenant_id_source_lead_id",
                schema: "leads",
                table: "lead_merges",
                columns: new[] { "tenant_id", "source_lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_merges_tenant_id_target_lead_id",
                schema: "leads",
                table: "lead_merges",
                columns: new[] { "tenant_id", "target_lead_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_merges",
                schema: "leads");
        }
    }
}
