using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QualifyLeadsWithProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A lead with a project is qualified (product owner, 2026-11-02): the leads that had one
            // before the rule, still in an early status, are moved as Lead.QualifyByProject would.
            // A data fix, so no lifecycle event or webhook is sent for them.
            migrationBuilder.Sql("""
                UPDATE leads.leads l
                SET status = 'Qualified',
                    pipeline_stage = CASE WHEN l.pipeline_stage IN ('New', 'ContactAttempted', 'ContactEstablished', 'NeedIdentified')
                                          THEN 'Qualified' ELSE l.pipeline_stage END,
                    updated_at = now()
                WHERE l.status IN ('New', 'Open', 'Qualifying', 'Nurturing', 'Recycled')
                  AND EXISTS (SELECT 1 FROM leads.opportunities o
                              WHERE o.lead_id = l.id AND o.tenant_id = l.tenant_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
