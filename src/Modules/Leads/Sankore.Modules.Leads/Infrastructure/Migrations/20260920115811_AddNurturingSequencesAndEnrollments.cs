using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNurturingSequencesAndEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nurturing_enrollments",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_completed_step_index = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_step_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancellation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nurturing_enrollments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nurturing_sequences",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nurturing_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nurturing_steps",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    delay_from_previous = table.Column<TimeSpan>(type: "interval", nullable: false),
                    email_template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nurturing_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_nurturing_steps_nurturing_sequences_sequence_id",
                        column: x => x.sequence_id,
                        principalSchema: "leads",
                        principalTable: "nurturing_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nurturing_enrollments_lead_id_status",
                schema: "leads",
                table: "nurturing_enrollments",
                columns: new[] { "lead_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_nurturing_enrollments_tenant_id_status_next_step_due_at",
                schema: "leads",
                table: "nurturing_enrollments",
                columns: new[] { "tenant_id", "status", "next_step_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nurturing_steps_sequence_id_order",
                schema: "leads",
                table: "nurturing_steps",
                columns: new[] { "sequence_id", "order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nurturing_enrollments",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "nurturing_steps",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "nurturing_sequences",
                schema: "leads");
        }
    }
}
