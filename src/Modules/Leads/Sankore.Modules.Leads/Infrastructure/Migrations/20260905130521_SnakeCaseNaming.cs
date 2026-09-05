using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_lead_assignments_leads_LeadId",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_outbox_messages",
                schema: "leads",
                table: "outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_leads",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropPrimaryKey(
                name: "PK_lead_assignments",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_dispatching_rules",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "leads",
                table: "outbox_messages",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RetryCount",
                schema: "leads",
                table: "outbox_messages",
                newName: "retry_count");

            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "leads",
                table: "outbox_messages",
                newName: "processed_at");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                schema: "leads",
                table: "outbox_messages",
                newName: "payload_json");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "leads",
                table: "outbox_messages",
                newName: "occurred_at");

            migrationBuilder.RenameColumn(
                name: "LastError",
                schema: "leads",
                table: "outbox_messages",
                newName: "last_error");

            migrationBuilder.RenameColumn(
                name: "EventType",
                schema: "leads",
                table: "outbox_messages",
                newName: "event_type");

            migrationBuilder.RenameIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                schema: "leads",
                table: "outbox_messages",
                newName: "ix_outbox_messages_processed_at_occurred_at");

            migrationBuilder.RenameColumn(
                name: "Website",
                schema: "leads",
                table: "leads",
                newName: "website");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "leads",
                table: "leads",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Source",
                schema: "leads",
                table: "leads",
                newName: "source");

            migrationBuilder.RenameColumn(
                name: "Score",
                schema: "leads",
                table: "leads",
                newName: "score");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "leads",
                table: "leads",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "leads",
                table: "leads",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "PreferredLanguage",
                schema: "leads",
                table: "leads",
                newName: "preferred_language");

            migrationBuilder.RenameColumn(
                name: "PreferredAgencyId",
                schema: "leads",
                table: "leads",
                newName: "preferred_agency_id");

            migrationBuilder.RenameColumn(
                name: "PhoneNumber",
                schema: "leads",
                table: "leads",
                newName: "phone_number");

            migrationBuilder.RenameColumn(
                name: "LossReason",
                schema: "leads",
                table: "leads",
                newName: "loss_reason");

            migrationBuilder.RenameColumn(
                name: "InterestedProduct",
                schema: "leads",
                table: "leads",
                newName: "interested_product");

            migrationBuilder.RenameColumn(
                name: "FullName",
                schema: "leads",
                table: "leads",
                newName: "full_name");

            migrationBuilder.RenameColumn(
                name: "ExpiresAt",
                schema: "leads",
                table: "leads",
                newName: "expires_at");

            migrationBuilder.RenameColumn(
                name: "CurrentAssignmentId",
                schema: "leads",
                table: "leads",
                newName: "current_assignment_id");

            migrationBuilder.RenameColumn(
                name: "CurrentAssignedId",
                schema: "leads",
                table: "leads",
                newName: "current_assigned_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "leads",
                table: "leads",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CompanyPhone",
                schema: "leads",
                table: "leads",
                newName: "company_phone");

            migrationBuilder.RenameColumn(
                name: "CompanyName",
                schema: "leads",
                table: "leads",
                newName: "company_name");

            migrationBuilder.RenameColumn(
                name: "CompanyEmail",
                schema: "leads",
                table: "leads",
                newName: "company_email");

            migrationBuilder.RenameColumn(
                name: "CompanyAddress",
                schema: "leads",
                table: "leads",
                newName: "company_address");

            migrationBuilder.RenameColumn(
                name: "AgentCollectedLeadId",
                schema: "leads",
                table: "leads",
                newName: "agent_collected_lead_id");

            migrationBuilder.RenameIndex(
                name: "IX_leads_TenantId_Status",
                schema: "leads",
                table: "leads",
                newName: "ix_leads_tenant_id_status");

            migrationBuilder.RenameIndex(
                name: "IX_leads_PhoneNumber",
                schema: "leads",
                table: "leads",
                newName: "ix_leads_phone_number");

            migrationBuilder.RenameIndex(
                name: "IX_leads_FullName",
                schema: "leads",
                table: "leads",
                newName: "ix_leads_full_name");

            migrationBuilder.RenameColumn(
                name: "Strategy",
                schema: "leads",
                table: "lead_assignments",
                newName: "strategy");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "leads",
                table: "lead_assignments",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "WasManualOverride",
                schema: "leads",
                table: "lead_assignments",
                newName: "was_manual_override");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "leads",
                table: "lead_assignments",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "SlaDeadline",
                schema: "leads",
                table: "lead_assignments",
                newName: "sla_deadline");

            migrationBuilder.RenameColumn(
                name: "OverrideReason",
                schema: "leads",
                table: "lead_assignments",
                newName: "override_reason");

            migrationBuilder.RenameColumn(
                name: "LeadId",
                schema: "leads",
                table: "lead_assignments",
                newName: "lead_id");

            migrationBuilder.RenameColumn(
                name: "FirstContactAt",
                schema: "leads",
                table: "lead_assignments",
                newName: "first_contact_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "leads",
                table: "lead_assignments",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CompatibilityScore",
                schema: "leads",
                table: "lead_assignments",
                newName: "compatibility_score");

            migrationBuilder.RenameColumn(
                name: "AgentId",
                schema: "leads",
                table: "lead_assignments",
                newName: "agent_id");

            migrationBuilder.RenameIndex(
                name: "IX_lead_assignments_SlaDeadline_FirstContactAt",
                schema: "leads",
                table: "lead_assignments",
                newName: "ix_lead_assignments_sla_deadline_first_contact_at");

            migrationBuilder.RenameIndex(
                name: "IX_lead_assignments_LeadId",
                schema: "leads",
                table: "lead_assignments",
                newName: "ix_lead_assignments_lead_id");

            migrationBuilder.RenameIndex(
                name: "IX_lead_assignments_AgentId",
                schema: "leads",
                table: "lead_assignments",
                newName: "ix_lead_assignments_agent_id");

            migrationBuilder.RenameColumn(
                name: "Strategy",
                schema: "leads",
                table: "dispatching_rules",
                newName: "strategy");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "leads",
                table: "dispatching_rules",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "leads",
                table: "dispatching_rules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "leads",
                table: "dispatching_rules",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "MaxLeadsPerAgent",
                schema: "leads",
                table: "dispatching_rules",
                newName: "max_leads_per_agent");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "leads",
                table: "dispatching_rules",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "FirstContactSla",
                schema: "leads",
                table: "dispatching_rules",
                newName: "first_contact_sla");

            migrationBuilder.RenameColumn(
                name: "AntiMonopolyThreshold",
                schema: "leads",
                table: "dispatching_rules",
                newName: "anti_monopoly_threshold");

            migrationBuilder.RenameIndex(
                name: "IX_dispatching_rules_TenantId_IsActive",
                schema: "leads",
                table: "dispatching_rules",
                newName: "ix_dispatching_rules_tenant_id_is_active");

            migrationBuilder.AddPrimaryKey(
                name: "pk_outbox_messages",
                schema: "leads",
                table: "outbox_messages",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_leads",
                schema: "leads",
                table: "leads",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_lead_assignments",
                schema: "leads",
                table: "lead_assignments",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_dispatching_rules",
                schema: "leads",
                table: "dispatching_rules",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_lead_assignments_leads_lead_id",
                schema: "leads",
                table: "lead_assignments",
                column: "lead_id",
                principalSchema: "leads",
                principalTable: "leads",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_lead_assignments_leads_lead_id",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_outbox_messages",
                schema: "leads",
                table: "outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "pk_leads",
                schema: "leads",
                table: "leads");

            migrationBuilder.DropPrimaryKey(
                name: "pk_lead_assignments",
                schema: "leads",
                table: "lead_assignments");

            migrationBuilder.DropPrimaryKey(
                name: "pk_dispatching_rules",
                schema: "leads",
                table: "dispatching_rules");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "leads",
                table: "outbox_messages",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "retry_count",
                schema: "leads",
                table: "outbox_messages",
                newName: "RetryCount");

            migrationBuilder.RenameColumn(
                name: "processed_at",
                schema: "leads",
                table: "outbox_messages",
                newName: "ProcessedAt");

            migrationBuilder.RenameColumn(
                name: "payload_json",
                schema: "leads",
                table: "outbox_messages",
                newName: "PayloadJson");

            migrationBuilder.RenameColumn(
                name: "occurred_at",
                schema: "leads",
                table: "outbox_messages",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "last_error",
                schema: "leads",
                table: "outbox_messages",
                newName: "LastError");

            migrationBuilder.RenameColumn(
                name: "event_type",
                schema: "leads",
                table: "outbox_messages",
                newName: "EventType");

            migrationBuilder.RenameIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "leads",
                table: "outbox_messages",
                newName: "IX_outbox_messages_ProcessedAt_OccurredAt");

            migrationBuilder.RenameColumn(
                name: "website",
                schema: "leads",
                table: "leads",
                newName: "Website");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "leads",
                table: "leads",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "source",
                schema: "leads",
                table: "leads",
                newName: "Source");

            migrationBuilder.RenameColumn(
                name: "score",
                schema: "leads",
                table: "leads",
                newName: "Score");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "leads",
                table: "leads",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "leads",
                table: "leads",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "preferred_language",
                schema: "leads",
                table: "leads",
                newName: "PreferredLanguage");

            migrationBuilder.RenameColumn(
                name: "preferred_agency_id",
                schema: "leads",
                table: "leads",
                newName: "PreferredAgencyId");

            migrationBuilder.RenameColumn(
                name: "phone_number",
                schema: "leads",
                table: "leads",
                newName: "PhoneNumber");

            migrationBuilder.RenameColumn(
                name: "loss_reason",
                schema: "leads",
                table: "leads",
                newName: "LossReason");

            migrationBuilder.RenameColumn(
                name: "interested_product",
                schema: "leads",
                table: "leads",
                newName: "InterestedProduct");

            migrationBuilder.RenameColumn(
                name: "full_name",
                schema: "leads",
                table: "leads",
                newName: "FullName");

            migrationBuilder.RenameColumn(
                name: "expires_at",
                schema: "leads",
                table: "leads",
                newName: "ExpiresAt");

            migrationBuilder.RenameColumn(
                name: "current_assignment_id",
                schema: "leads",
                table: "leads",
                newName: "CurrentAssignmentId");

            migrationBuilder.RenameColumn(
                name: "current_assigned_id",
                schema: "leads",
                table: "leads",
                newName: "CurrentAssignedId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "leads",
                table: "leads",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "company_phone",
                schema: "leads",
                table: "leads",
                newName: "CompanyPhone");

            migrationBuilder.RenameColumn(
                name: "company_name",
                schema: "leads",
                table: "leads",
                newName: "CompanyName");

            migrationBuilder.RenameColumn(
                name: "company_email",
                schema: "leads",
                table: "leads",
                newName: "CompanyEmail");

            migrationBuilder.RenameColumn(
                name: "company_address",
                schema: "leads",
                table: "leads",
                newName: "CompanyAddress");

            migrationBuilder.RenameColumn(
                name: "agent_collected_lead_id",
                schema: "leads",
                table: "leads",
                newName: "AgentCollectedLeadId");

            migrationBuilder.RenameIndex(
                name: "ix_leads_tenant_id_status",
                schema: "leads",
                table: "leads",
                newName: "IX_leads_TenantId_Status");

            migrationBuilder.RenameIndex(
                name: "ix_leads_phone_number",
                schema: "leads",
                table: "leads",
                newName: "IX_leads_PhoneNumber");

            migrationBuilder.RenameIndex(
                name: "ix_leads_full_name",
                schema: "leads",
                table: "leads",
                newName: "IX_leads_FullName");

            migrationBuilder.RenameColumn(
                name: "strategy",
                schema: "leads",
                table: "lead_assignments",
                newName: "Strategy");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "leads",
                table: "lead_assignments",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "was_manual_override",
                schema: "leads",
                table: "lead_assignments",
                newName: "WasManualOverride");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "leads",
                table: "lead_assignments",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "sla_deadline",
                schema: "leads",
                table: "lead_assignments",
                newName: "SlaDeadline");

            migrationBuilder.RenameColumn(
                name: "override_reason",
                schema: "leads",
                table: "lead_assignments",
                newName: "OverrideReason");

            migrationBuilder.RenameColumn(
                name: "lead_id",
                schema: "leads",
                table: "lead_assignments",
                newName: "LeadId");

            migrationBuilder.RenameColumn(
                name: "first_contact_at",
                schema: "leads",
                table: "lead_assignments",
                newName: "FirstContactAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "leads",
                table: "lead_assignments",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "compatibility_score",
                schema: "leads",
                table: "lead_assignments",
                newName: "CompatibilityScore");

            migrationBuilder.RenameColumn(
                name: "agent_id",
                schema: "leads",
                table: "lead_assignments",
                newName: "AgentId");

            migrationBuilder.RenameIndex(
                name: "ix_lead_assignments_sla_deadline_first_contact_at",
                schema: "leads",
                table: "lead_assignments",
                newName: "IX_lead_assignments_SlaDeadline_FirstContactAt");

            migrationBuilder.RenameIndex(
                name: "ix_lead_assignments_lead_id",
                schema: "leads",
                table: "lead_assignments",
                newName: "IX_lead_assignments_LeadId");

            migrationBuilder.RenameIndex(
                name: "ix_lead_assignments_agent_id",
                schema: "leads",
                table: "lead_assignments",
                newName: "IX_lead_assignments_AgentId");

            migrationBuilder.RenameColumn(
                name: "strategy",
                schema: "leads",
                table: "dispatching_rules",
                newName: "Strategy");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "leads",
                table: "dispatching_rules",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "leads",
                table: "dispatching_rules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "leads",
                table: "dispatching_rules",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "max_leads_per_agent",
                schema: "leads",
                table: "dispatching_rules",
                newName: "MaxLeadsPerAgent");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "leads",
                table: "dispatching_rules",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "first_contact_sla",
                schema: "leads",
                table: "dispatching_rules",
                newName: "FirstContactSla");

            migrationBuilder.RenameColumn(
                name: "anti_monopoly_threshold",
                schema: "leads",
                table: "dispatching_rules",
                newName: "AntiMonopolyThreshold");

            migrationBuilder.RenameIndex(
                name: "ix_dispatching_rules_tenant_id_is_active",
                schema: "leads",
                table: "dispatching_rules",
                newName: "IX_dispatching_rules_TenantId_IsActive");

            migrationBuilder.AddPrimaryKey(
                name: "PK_outbox_messages",
                schema: "leads",
                table: "outbox_messages",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_leads",
                schema: "leads",
                table: "leads",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_lead_assignments",
                schema: "leads",
                table: "lead_assignments",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_dispatching_rules",
                schema: "leads",
                table: "dispatching_rules",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_lead_assignments_leads_LeadId",
                schema: "leads",
                table: "lead_assignments",
                column: "LeadId",
                principalSchema: "leads",
                principalTable: "leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
