using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadSourceConfigIdToLead : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "lead_source_config_id",
                schema: "leads",
                table: "leads",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_leads_tenant_id_lead_source_config_id",
                schema: "leads",
                table: "leads",
                columns: new[] { "tenant_id", "lead_source_config_id" },
                filter: "lead_source_config_id IS NOT NULL");

            // Backfill from the side table that held this link until now. Every clause carries
            // weight:
            //
            //  status = 'Accepted'  — the column is mapped HasConversion<string>(), so this
            //      compares against the enum NAME, not an integer. Accepted-only is the
            //      important part: a 'Duplicate' row points at the SURVIVING lead while
            //      carrying the INCOMING source id, so including those rows would reassign a
            //      lead's provenance to a source whose payload was actually dropped.
            //      'Rejected' and 'Failed' never produced a lead at all.
            //
            //  lead_id <> uuid-zero — excludes the CreateFailed sentinel, and keeps the
            //      DISTINCT ON groups honest (otherwise every failed row of a tenant collapses
            //      into one group).
            //
            //  ingested_at DESC, id DESC — most recent accepted ingestion wins. ingested_at is
            //      the chronological column; id is a random v4 Guid and is used here ONLY as an
            //      intra-timestamp tiebreaker, never as a stand-in for time. (Conflating the two
            //      is precisely the defect being deleted from DispatchingRuleResolver.)
            //
            //  IS NULL              — makes the statement safe to re-run, which matters for the
            //      rollout window: an instance still running the old code can create a lead
            //      with an ingestion row and no column, and re-running this fixes it without
            //      touching anything already set.
            migrationBuilder.Sql("""
                UPDATE leads.leads AS l
                SET    lead_source_config_id = i.source_id
                FROM (
                    SELECT DISTINCT ON (tenant_id, lead_id) tenant_id, lead_id, source_id
                    FROM   leads.lead_ingestions
                    WHERE  lead_id <> '00000000-0000-0000-0000-000000000000'
                      AND  status = 'Accepted'
                    ORDER  BY tenant_id, lead_id, ingested_at DESC, id DESC
                ) AS i
                WHERE  l.id        = i.lead_id
                  AND  l.tenant_id = i.tenant_id
                  AND  l.lead_source_config_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_leads_tenant_id_lead_source_config_id",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropColumn(
                name: "lead_source_config_id",
                schema: "leads",
                table: "leads");
        }
    }
}
