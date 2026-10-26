using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceLots3And4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_payment_instruments",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    encrypted_identifier = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    masked_identifier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    holder_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_payment_instruments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suspicion_alerts",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    encrypted_facts = table.Column<string>(type: "text", nullable: false),
                    raised_by = table.Column<Guid>(type: "uuid", nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    handled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    handled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    declared_on = table.Column<DateOnly>(type: "date", nullable: true),
                    declaration_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    encrypted_notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suspicion_alerts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_payment_instruments_tenant_id_client_id",
                schema: "customers",
                table: "client_payment_instruments",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_suspicion_alerts_tenant_id_status",
                schema: "customers",
                table: "suspicion_alerts",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_payment_instruments",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "suspicion_alerts",
                schema: "customers");
        }
    }
}
