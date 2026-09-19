using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadOwnerAssignmentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_owner_assignment_histories",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    new_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_owner_assignment_histories", x => x.id);
                    table.ForeignKey(
                        name: "fk_lead_owner_assignment_histories_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_owner_assignment_histories_lead_id_assigned_at",
                schema: "leads",
                table: "lead_owner_assignment_histories",
                columns: new[] { "lead_id", "assigned_at" });

            migrationBuilder.CreateIndex(
                name: "ix_lead_owner_assignment_histories_tenant_id_new_owner_id",
                schema: "leads",
                table: "lead_owner_assignment_histories",
                columns: new[] { "tenant_id", "new_owner_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_owner_assignment_histories",
                schema: "leads");
        }
    }
}
