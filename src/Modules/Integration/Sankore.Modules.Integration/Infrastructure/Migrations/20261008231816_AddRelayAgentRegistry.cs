using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRelayAgentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_relay_agent",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enrolment_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enrolment_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    certificate_thumbprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    certificate_issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reported_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    reported_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    reported_status_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_relay_agent", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_relay_agent_tenant_status",
                schema: "integration",
                table: "integration_relay_agent",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_relay_agent_certificate",
                schema: "integration",
                table: "integration_relay_agent",
                column: "certificate_thumbprint",
                unique: true,
                filter: "certificate_thumbprint IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_integration_relay_agent_enrolment_token",
                schema: "integration",
                table: "integration_relay_agent",
                column: "enrolment_token_hash",
                unique: true,
                filter: "enrolment_token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_relay_agent",
                schema: "integration");
        }
    }
}
