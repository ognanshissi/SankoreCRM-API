using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <summary>
    /// The complaints register of the règlement n° 01/CIMA/PCMA/CE/SG/2024 — the only genuinely
    /// new table of the insurance family.
    ///
    /// <para>
    /// Nothing else in the schema could carry it: a complaint may name no policy at all (a
    /// prospect complaining about a quote), may outlive the contract it is about, and has
    /// deadlines of its own belonging to neither the policy nor the claim.
    /// </para>
    /// </summary>
    public partial class AddInsuranceComplaintRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: ins_complaint maps a concurrency token onto PostgreSQL's xmin SYSTEM column.
            // EF scaffolds that as a real column, which CREATE TABLE refuses:
            //     ERROR: column name "xmin" conflicts with a system column name
            // xmin already exists on every table, so mapping it is a read-only binding and not a
            // schema change — the scaffolded line was removed by hand. The model snapshot keeps
            // the mapping, which is what matters at run time.

            migrationBuilder.CreateTable(
                name: "ins_complaint",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_encrypted = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_by = table.Column<Guid>(type: "uuid", nullable: false),
                    acknowledgement_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolution_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<Guid>(type: "uuid", nullable: true),
                    escalated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    escalated_to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution = table.Column<string>(type: "text", nullable: true),
                    was_upheld = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_complaint", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_complaint_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ins_complaint_connection_id",
                schema: "integration",
                table: "ins_complaint",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_complaint_customer",
                schema: "integration",
                table: "ins_complaint",
                columns: new[] { "tenant_id", "crm_customer_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_complaint_open_due",
                schema: "integration",
                table: "ins_complaint",
                columns: new[] { "tenant_id", "status", "resolution_due_at" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_complaint_reference",
                schema: "integration",
                table: "ins_complaint",
                columns: new[] { "tenant_id", "reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ins_complaint",
                schema: "integration");
        }
    }
}
