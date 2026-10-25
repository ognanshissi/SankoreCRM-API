using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddErasureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processing_restricted_at",
                schema: "customers",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "erasure_requests",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_reference = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    evidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    eligible_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    blocker = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_erasure_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_requests_tenant_id_status_requested_at",
                schema: "customers",
                table: "erasure_requests",
                columns: new[] { "tenant_id", "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_erasure_requests_open_subject",
                schema: "customers",
                table: "erasure_requests",
                columns: new[] { "tenant_id", "subject_type", "subject_id" },
                unique: true,
                filter: "status IN ('Pending', 'Restricted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "erasure_requests",
                schema: "customers");

            migrationBuilder.DropColumn(
                name: "processing_restricted_at",
                schema: "customers",
                table: "clients");
        }
    }
}
