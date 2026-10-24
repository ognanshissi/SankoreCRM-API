using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveLeadApplicationStagesToProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Projects, lot 4: a lead stops at « besoin identifié »; qualification, proposal,
            // application, documents and approval are a PROJECT's steps now. Every OPEN lead still
            // on one of those stages gets a project at the equivalent phase — unless it already has
            // an open one — and goes back to NeedIdentified. Closed leads (lost, disqualified,
            // archived) are history and keep the stage they ended on; converted ones are already
            // on Converted.
            //
            // The phases keep the old order, so no project lands « behind » where its lead was:
            // Qualified → Qualification, ProductProposed → Proposal, anything about the
            // application itself → Negotiation. The probability is the phase's standard one.
            migrationBuilder.Sql("""
                INSERT INTO leads.opportunities
                    (id, tenant_id, lead_id, customer_entity_type, customer_entity_id, title, description, product,
                     estimated_amount, estimated_currency, stage, probability, owner_id, created_at, updated_at)
                SELECT gen_random_uuid(),
                       l.tenant_id,
                       l.id,
                       CASE WHEN l.customer_id IS NOT NULL THEN 'Customer' END,
                       l.customer_id,
                       left('Projet — ' || COALESCE(NULLIF(NULLIF(btrim(l.interested_product), ''), 'Unknown'), 'à préciser'), 200),
                       'Repris du pipeline des leads (étape « ' || l.pipeline_stage || ' »).',
                       left(COALESCE(NULLIF(NULLIF(btrim(l.interested_product), ''), 'Unknown'), ''), 100),
                       l.desired_amount,
                       l.desired_currency,
                       CASE l.pipeline_stage
                           WHEN 'Qualified'       THEN 'Qualification'
                           WHEN 'ProductProposed' THEN 'Proposal'
                           ELSE 'Negotiation'
                       END,
                       CASE l.pipeline_stage
                           WHEN 'Qualified'       THEN 0.20
                           WHEN 'ProductProposed' THEN 0.60
                           ELSE 0.80
                       END,
                       l.current_assigned_id,
                       now(),
                       now()
                  FROM leads.leads l
                 WHERE l.pipeline_stage IN ('Qualified', 'ProductProposed', 'ApplicationStarted',
                                            'DocumentCollection', 'ApplicationCompleted', 'ApprovalPending')
                   AND l.status NOT IN ('Converted', 'Lost', 'Disqualified', 'Archived')
                   AND NOT EXISTS (
                       SELECT 1 FROM leads.opportunities o
                        WHERE o.lead_id = l.id
                          AND o.stage NOT IN ('ClosedWon', 'ClosedLost'));

                UPDATE leads.leads
                   SET pipeline_stage = 'NeedIdentified',
                       updated_at = now()
                 WHERE pipeline_stage IN ('Qualified', 'ProductProposed', 'ApplicationStarted',
                                          'DocumentCollection', 'ApplicationCompleted', 'ApprovalPending')
                   AND status NOT IN ('Converted', 'Lost', 'Disqualified', 'Archived');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: which stage each lead was on is only kept in the projects' description,
            // and the projects may have moved since. Rolling the schema back leaves the data as is.
        }
    }
}
