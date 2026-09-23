using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadIngestionAndSourceRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_source_runs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fetched_count = table.Column<int>(type: "integer", nullable: false),
                    ingested_count = table.Column<int>(type: "integer", nullable: false),
                    rejected_count = table.Column<int>(type: "integer", nullable: false),
                    duplicate_count = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_source_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "lead_ingestions",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    raw_payload_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ingested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_ingestions", x => x.id);
                    table.ForeignKey(
                        name: "fk_lead_ingestions_lead_source_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "leads",
                        principalTable: "lead_source_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_ingestions_run_id",
                schema: "leads",
                table: "lead_ingestions",
                column: "run_id",
                filter: "run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lead_ingestions_tenant_id_lead_id",
                schema: "leads",
                table: "lead_ingestions",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_ingestions_tenant_id_source_id",
                schema: "leads",
                table: "lead_ingestions",
                columns: new[] { "tenant_id", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_source_runs_tenant_id_source_id_started_at",
                schema: "leads",
                table: "lead_source_runs",
                columns: new[] { "tenant_id", "source_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_ingestions",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "lead_source_runs",
                schema: "leads");
        }
    }
}
