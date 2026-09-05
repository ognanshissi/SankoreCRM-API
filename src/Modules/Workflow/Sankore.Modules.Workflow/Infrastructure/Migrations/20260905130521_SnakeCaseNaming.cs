using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_instance_steps_workflow_instances_InstanceId",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_step_definitions_workflow_templates_TemplateId",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_templates",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_step_definitions",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_instances",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_instance_steps",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropPrimaryKey(
                name: "PK_outbox_messages",
                schema: "workflow",
                table: "outbox_messages");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "workflow",
                table: "workflow_templates",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Description",
                schema: "workflow",
                table: "workflow_templates",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "workflow",
                table: "workflow_templates",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "workflow",
                table: "workflow_templates",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "workflow",
                table: "workflow_templates",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "workflow",
                table: "workflow_templates",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "EntityType",
                schema: "workflow",
                table: "workflow_templates",
                newName: "entity_type");

            migrationBuilder.RenameColumn(
                name: "CreatedByUserId",
                schema: "workflow",
                table: "workflow_templates",
                newName: "created_by_user_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "workflow",
                table: "workflow_templates",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_workflow_templates_TenantId_EntityType",
                schema: "workflow",
                table: "workflow_templates",
                newName: "ix_workflow_templates_tenant_id_entity_type");

            migrationBuilder.RenameColumn(
                name: "Order",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "order");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Description",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TimeoutHours",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "timeout_hours");

            migrationBuilder.RenameColumn(
                name: "TemplateId",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "template_id");

            migrationBuilder.RenameColumn(
                name: "ApproverRoleCode",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "approver_role_code");

            migrationBuilder.RenameIndex(
                name: "IX_workflow_step_definitions_TemplateId_Order",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "ix_workflow_step_definitions_template_id_order");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "workflow",
                table: "workflow_instances",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "workflow",
                table: "workflow_instances",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "TemplateId",
                schema: "workflow",
                table: "workflow_instances",
                newName: "template_id");

            migrationBuilder.RenameColumn(
                name: "StartedByUserId",
                schema: "workflow",
                table: "workflow_instances",
                newName: "started_by_user_id");

            migrationBuilder.RenameColumn(
                name: "StartedAt",
                schema: "workflow",
                table: "workflow_instances",
                newName: "started_at");

            migrationBuilder.RenameColumn(
                name: "EntityType",
                schema: "workflow",
                table: "workflow_instances",
                newName: "entity_type");

            migrationBuilder.RenameColumn(
                name: "EntityId",
                schema: "workflow",
                table: "workflow_instances",
                newName: "entity_id");

            migrationBuilder.RenameColumn(
                name: "CurrentStepOrder",
                schema: "workflow",
                table: "workflow_instances",
                newName: "current_step_order");

            migrationBuilder.RenameColumn(
                name: "CompletedAt",
                schema: "workflow",
                table: "workflow_instances",
                newName: "completed_at");

            migrationBuilder.RenameIndex(
                name: "IX_workflow_instances_TenantId_EntityType_EntityId",
                schema: "workflow",
                table: "workflow_instances",
                newName: "ix_workflow_instances_tenant_id_entity_type_entity_id");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Order",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "order");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Comment",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "comment");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "StepDefinitionId",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "step_definition_id");

            migrationBuilder.RenameColumn(
                name: "InstanceId",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "instance_id");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "CompletedAt",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "completed_at");

            migrationBuilder.RenameColumn(
                name: "ApproverRoleCode",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "approver_role_code");

            migrationBuilder.RenameColumn(
                name: "ActedByUserId",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "acted_by_user_id");

            migrationBuilder.RenameIndex(
                name: "IX_workflow_instance_steps_InstanceId_Order",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "ix_workflow_instance_steps_instance_id_order");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "workflow",
                table: "outbox_messages",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RetryCount",
                schema: "workflow",
                table: "outbox_messages",
                newName: "retry_count");

            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "workflow",
                table: "outbox_messages",
                newName: "processed_at");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                schema: "workflow",
                table: "outbox_messages",
                newName: "payload_json");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "workflow",
                table: "outbox_messages",
                newName: "occurred_at");

            migrationBuilder.RenameColumn(
                name: "LastError",
                schema: "workflow",
                table: "outbox_messages",
                newName: "last_error");

            migrationBuilder.RenameColumn(
                name: "EventType",
                schema: "workflow",
                table: "outbox_messages",
                newName: "event_type");

            migrationBuilder.RenameIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                schema: "workflow",
                table: "outbox_messages",
                newName: "ix_outbox_messages_processed_at_occurred_at");

            migrationBuilder.AddPrimaryKey(
                name: "pk_workflow_templates",
                schema: "workflow",
                table: "workflow_templates",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_workflow_step_definitions",
                schema: "workflow",
                table: "workflow_step_definitions",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_workflow_instances",
                schema: "workflow",
                table: "workflow_instances",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_workflow_instance_steps",
                schema: "workflow",
                table: "workflow_instance_steps",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_outbox_messages",
                schema: "workflow",
                table: "outbox_messages",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_instance_steps_workflow_instances_instance_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                column: "instance_id",
                principalSchema: "workflow",
                principalTable: "workflow_instances",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_step_definitions_workflow_templates_template_id",
                schema: "workflow",
                table: "workflow_step_definitions",
                column: "template_id",
                principalSchema: "workflow",
                principalTable: "workflow_templates",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_workflow_instance_steps_workflow_instances_instance_id",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropForeignKey(
                name: "fk_workflow_step_definitions_workflow_templates_template_id",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_workflow_templates",
                schema: "workflow",
                table: "workflow_templates");

            migrationBuilder.DropPrimaryKey(
                name: "pk_workflow_step_definitions",
                schema: "workflow",
                table: "workflow_step_definitions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_workflow_instances",
                schema: "workflow",
                table: "workflow_instances");

            migrationBuilder.DropPrimaryKey(
                name: "pk_workflow_instance_steps",
                schema: "workflow",
                table: "workflow_instance_steps");

            migrationBuilder.DropPrimaryKey(
                name: "pk_outbox_messages",
                schema: "workflow",
                table: "outbox_messages");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "workflow",
                table: "workflow_templates",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "description",
                schema: "workflow",
                table: "workflow_templates",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "workflow",
                table: "workflow_templates",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "workflow",
                table: "workflow_templates",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "workflow",
                table: "workflow_templates",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "workflow",
                table: "workflow_templates",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "entity_type",
                schema: "workflow",
                table: "workflow_templates",
                newName: "EntityType");

            migrationBuilder.RenameColumn(
                name: "created_by_user_id",
                schema: "workflow",
                table: "workflow_templates",
                newName: "CreatedByUserId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "workflow",
                table: "workflow_templates",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_workflow_templates_tenant_id_entity_type",
                schema: "workflow",
                table: "workflow_templates",
                newName: "IX_workflow_templates_TenantId_EntityType");

            migrationBuilder.RenameColumn(
                name: "order",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "Order");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "description",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "timeout_hours",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "TimeoutHours");

            migrationBuilder.RenameColumn(
                name: "template_id",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "TemplateId");

            migrationBuilder.RenameColumn(
                name: "approver_role_code",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "ApproverRoleCode");

            migrationBuilder.RenameIndex(
                name: "ix_workflow_step_definitions_template_id_order",
                schema: "workflow",
                table: "workflow_step_definitions",
                newName: "IX_workflow_step_definitions_TemplateId_Order");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "workflow",
                table: "workflow_instances",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "template_id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "TemplateId");

            migrationBuilder.RenameColumn(
                name: "started_by_user_id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "StartedByUserId");

            migrationBuilder.RenameColumn(
                name: "started_at",
                schema: "workflow",
                table: "workflow_instances",
                newName: "StartedAt");

            migrationBuilder.RenameColumn(
                name: "entity_type",
                schema: "workflow",
                table: "workflow_instances",
                newName: "EntityType");

            migrationBuilder.RenameColumn(
                name: "entity_id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "EntityId");

            migrationBuilder.RenameColumn(
                name: "current_step_order",
                schema: "workflow",
                table: "workflow_instances",
                newName: "CurrentStepOrder");

            migrationBuilder.RenameColumn(
                name: "completed_at",
                schema: "workflow",
                table: "workflow_instances",
                newName: "CompletedAt");

            migrationBuilder.RenameIndex(
                name: "ix_workflow_instances_tenant_id_entity_type_entity_id",
                schema: "workflow",
                table: "workflow_instances",
                newName: "IX_workflow_instances_TenantId_EntityType_EntityId");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "order",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "Order");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "comment",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "Comment");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "step_definition_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "StepDefinitionId");

            migrationBuilder.RenameColumn(
                name: "instance_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "InstanceId");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "completed_at",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "CompletedAt");

            migrationBuilder.RenameColumn(
                name: "approver_role_code",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "ApproverRoleCode");

            migrationBuilder.RenameColumn(
                name: "acted_by_user_id",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "ActedByUserId");

            migrationBuilder.RenameIndex(
                name: "ix_workflow_instance_steps_instance_id_order",
                schema: "workflow",
                table: "workflow_instance_steps",
                newName: "IX_workflow_instance_steps_InstanceId_Order");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "workflow",
                table: "outbox_messages",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "retry_count",
                schema: "workflow",
                table: "outbox_messages",
                newName: "RetryCount");

            migrationBuilder.RenameColumn(
                name: "processed_at",
                schema: "workflow",
                table: "outbox_messages",
                newName: "ProcessedAt");

            migrationBuilder.RenameColumn(
                name: "payload_json",
                schema: "workflow",
                table: "outbox_messages",
                newName: "PayloadJson");

            migrationBuilder.RenameColumn(
                name: "occurred_at",
                schema: "workflow",
                table: "outbox_messages",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "last_error",
                schema: "workflow",
                table: "outbox_messages",
                newName: "LastError");

            migrationBuilder.RenameColumn(
                name: "event_type",
                schema: "workflow",
                table: "outbox_messages",
                newName: "EventType");

            migrationBuilder.RenameIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "workflow",
                table: "outbox_messages",
                newName: "IX_outbox_messages_ProcessedAt_OccurredAt");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_templates",
                schema: "workflow",
                table: "workflow_templates",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_step_definitions",
                schema: "workflow",
                table: "workflow_step_definitions",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_instances",
                schema: "workflow",
                table: "workflow_instances",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_instance_steps",
                schema: "workflow",
                table: "workflow_instance_steps",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_outbox_messages",
                schema: "workflow",
                table: "outbox_messages",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_instance_steps_workflow_instances_InstanceId",
                schema: "workflow",
                table: "workflow_instance_steps",
                column: "InstanceId",
                principalSchema: "workflow",
                principalTable: "workflow_instances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_step_definitions_workflow_templates_TemplateId",
                schema: "workflow",
                table: "workflow_step_definitions",
                column: "TemplateId",
                principalSchema: "workflow",
                principalTable: "workflow_templates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
