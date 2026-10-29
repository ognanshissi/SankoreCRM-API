using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadExternalEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_external_events",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_event_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    matched_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_external_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_external_events_tenant_id_lead_id",
                schema: "leads",
                table: "lead_external_events",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ux_lead_external_event_idempotency",
                schema: "leads",
                table: "lead_external_events",
                columns: new[] { "tenant_id", "source_id", "external_event_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_external_events",
                schema: "leads");
        }
    }
}
