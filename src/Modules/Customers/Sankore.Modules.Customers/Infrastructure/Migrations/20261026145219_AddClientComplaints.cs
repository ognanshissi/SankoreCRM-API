using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientComplaints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_complaints",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    holding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_encrypted = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by = table.Column<Guid>(type: "uuid", nullable: false),
                    source_conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledgement_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    response_encrypted = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    acknowledgement_overdue_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution_overdue_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    anonymized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_complaints", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_complaints_tenant_id_client_id",
                schema: "customers",
                table: "client_complaints",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_complaints_tenant_id_reference",
                schema: "customers",
                table: "client_complaints",
                columns: new[] { "tenant_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_client_complaints_tenant_id_status_resolution_due_at",
                schema: "customers",
                table: "client_complaints",
                columns: new[] { "tenant_id", "status", "resolution_due_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_complaints",
                schema: "customers");
        }
    }
}
