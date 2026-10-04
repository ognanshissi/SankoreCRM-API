using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadAssignmentSupersededAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lead_assignments_sla_deadline_first_contact_at",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                schema: "leads",
                table: "lead_assignments",
                type: "timestamp with time zone",
                nullable: true);

            // Backfill, and it is the point of this migration as much as the column is.
            //
            // Every assignment that is no longer its lead's current one was stranded open: nothing
            // could ever stamp first_contact_at on it (RecordFirstContactHandler only touches
            // Lead.CurrentAssignmentId), so CheckSlaBreachesJob kept mailing an SLA-breach alert to
            // its former agent, every day, for as long as the lead stayed open. Adding the column
            // without this leaves all of that history still alerting.
            //
            // Stamped with the creation time of the assignment that replaced it — the moment it
            // actually stopped being current — and with now() only when there is no successor,
            // which is the return-to-queue case.
            migrationBuilder.Sql("""
                UPDATE leads.lead_assignments AS a
                SET superseded_at = COALESCE(
                        (SELECT MIN(b.created_at)
                           FROM leads.lead_assignments AS b
                          WHERE b.lead_id = a.lead_id
                            AND b.created_at > a.created_at),
                        now())
                FROM leads.leads AS l
                WHERE l.id = a.lead_id
                  AND (l.current_assignment_id IS NULL OR l.current_assignment_id <> a.id);
                """);

            migrationBuilder.CreateIndex(
                name: "ix_lead_assignments_sla_deadline_first_contact_at",
                schema: "leads",
                table: "lead_assignments",
                columns: new[] { "sla_deadline", "first_contact_at" },
                filter: "superseded_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lead_assignments_sla_deadline_first_contact_at",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.CreateIndex(
                name: "ix_lead_assignments_sla_deadline_first_contact_at",
                schema: "leads",
                table: "lead_assignments",
                columns: new[] { "sla_deadline", "first_contact_at" });
        }
    }
}
