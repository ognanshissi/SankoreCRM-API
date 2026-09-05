using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_email_templates",
                schema: "notifications",
                table: "email_templates");

            migrationBuilder.DropPrimaryKey(
                name: "PK_email_outbox_messages",
                schema: "notifications",
                table: "email_outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_email_delivery_logs",
                schema: "notifications",
                table: "email_delivery_logs");

            migrationBuilder.RenameColumn(
                name: "Version",
                schema: "notifications",
                table: "email_templates",
                newName: "version");

            migrationBuilder.RenameColumn(
                name: "Subject",
                schema: "notifications",
                table: "email_templates",
                newName: "subject");

            migrationBuilder.RenameColumn(
                name: "Locale",
                schema: "notifications",
                table: "email_templates",
                newName: "locale");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notifications",
                table: "email_templates",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TextBody",
                schema: "notifications",
                table: "email_templates",
                newName: "text_body");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "notifications",
                table: "email_templates",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "TemplateKey",
                schema: "notifications",
                table: "email_templates",
                newName: "template_key");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "notifications",
                table: "email_templates",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "HtmlBody",
                schema: "notifications",
                table: "email_templates",
                newName: "html_body");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "notifications",
                table: "email_templates",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_email_templates_TenantId_TemplateKey_Locale_Version",
                schema: "notifications",
                table: "email_templates",
                newName: "ix_email_templates_tenant_id_template_key_locale_version");

            migrationBuilder.RenameIndex(
                name: "IX_email_templates_TemplateKey_Locale_IsActive",
                schema: "notifications",
                table: "email_templates",
                newName: "ix_email_templates_template_key_locale_is_active");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Module",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "module");

            migrationBuilder.RenameColumn(
                name: "Locale",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "locale");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "TemplateKey",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "template_key");

            migrationBuilder.RenameColumn(
                name: "TemplateDataJson",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "template_data_json");

            migrationBuilder.RenameColumn(
                name: "SentAt",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "sent_at");

            migrationBuilder.RenameColumn(
                name: "RecipientName",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "recipient_name");

            migrationBuilder.RenameColumn(
                name: "RecipientEmail",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "recipient_email");

            migrationBuilder.RenameColumn(
                name: "LastError",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "last_error");

            migrationBuilder.RenameColumn(
                name: "LastAttemptAt",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "last_attempt_at");

            migrationBuilder.RenameColumn(
                name: "IdempotencyKey",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "idempotency_key");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AttemptCount",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "attempt_count");

            migrationBuilder.RenameIndex(
                name: "IX_email_outbox_messages_TenantId_Status",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "ix_email_outbox_messages_tenant_id_status");

            migrationBuilder.RenameIndex(
                name: "IX_email_outbox_messages_Status_CreatedAt",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "ix_email_outbox_messages_status_created_at");

            migrationBuilder.RenameIndex(
                name: "IX_email_outbox_messages_IdempotencyKey",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "ix_email_outbox_messages_idempotency_key");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "RecordedAt",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "recorded_at");

            migrationBuilder.RenameColumn(
                name: "RecipientEmail",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "recipient_email");

            migrationBuilder.RenameColumn(
                name: "RawPayload",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "raw_payload");

            migrationBuilder.RenameColumn(
                name: "OutboxMessageId",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "outbox_message_id");

            migrationBuilder.RenameColumn(
                name: "EventType",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "event_type");

            migrationBuilder.RenameIndex(
                name: "IX_email_delivery_logs_TenantId_RecordedAt",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "ix_email_delivery_logs_tenant_id_recorded_at");

            migrationBuilder.RenameIndex(
                name: "IX_email_delivery_logs_OutboxMessageId",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "ix_email_delivery_logs_outbox_message_id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_email_templates",
                schema: "notifications",
                table: "email_templates",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_email_outbox_messages",
                schema: "notifications",
                table: "email_outbox_messages",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_email_delivery_logs",
                schema: "notifications",
                table: "email_delivery_logs",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_email_templates",
                schema: "notifications",
                table: "email_templates");

            migrationBuilder.DropPrimaryKey(
                name: "pk_email_outbox_messages",
                schema: "notifications",
                table: "email_outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "pk_email_delivery_logs",
                schema: "notifications",
                table: "email_delivery_logs");

            migrationBuilder.RenameColumn(
                name: "version",
                schema: "notifications",
                table: "email_templates",
                newName: "Version");

            migrationBuilder.RenameColumn(
                name: "subject",
                schema: "notifications",
                table: "email_templates",
                newName: "Subject");

            migrationBuilder.RenameColumn(
                name: "locale",
                schema: "notifications",
                table: "email_templates",
                newName: "Locale");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notifications",
                table: "email_templates",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "text_body",
                schema: "notifications",
                table: "email_templates",
                newName: "TextBody");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "notifications",
                table: "email_templates",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "template_key",
                schema: "notifications",
                table: "email_templates",
                newName: "TemplateKey");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "notifications",
                table: "email_templates",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "html_body",
                schema: "notifications",
                table: "email_templates",
                newName: "HtmlBody");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "notifications",
                table: "email_templates",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_email_templates_tenant_id_template_key_locale_version",
                schema: "notifications",
                table: "email_templates",
                newName: "IX_email_templates_TenantId_TemplateKey_Locale_Version");

            migrationBuilder.RenameIndex(
                name: "ix_email_templates_template_key_locale_is_active",
                schema: "notifications",
                table: "email_templates",
                newName: "IX_email_templates_TemplateKey_Locale_IsActive");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "module",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "Module");

            migrationBuilder.RenameColumn(
                name: "locale",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "Locale");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "template_key",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "TemplateKey");

            migrationBuilder.RenameColumn(
                name: "template_data_json",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "TemplateDataJson");

            migrationBuilder.RenameColumn(
                name: "sent_at",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "SentAt");

            migrationBuilder.RenameColumn(
                name: "recipient_name",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "RecipientName");

            migrationBuilder.RenameColumn(
                name: "recipient_email",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "RecipientEmail");

            migrationBuilder.RenameColumn(
                name: "last_error",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "LastError");

            migrationBuilder.RenameColumn(
                name: "last_attempt_at",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "LastAttemptAt");

            migrationBuilder.RenameColumn(
                name: "idempotency_key",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "IdempotencyKey");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "attempt_count",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "AttemptCount");

            migrationBuilder.RenameIndex(
                name: "ix_email_outbox_messages_tenant_id_status",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "IX_email_outbox_messages_TenantId_Status");

            migrationBuilder.RenameIndex(
                name: "ix_email_outbox_messages_status_created_at",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "IX_email_outbox_messages_Status_CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_email_outbox_messages_idempotency_key",
                schema: "notifications",
                table: "email_outbox_messages",
                newName: "IX_email_outbox_messages_IdempotencyKey");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "recorded_at",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "RecordedAt");

            migrationBuilder.RenameColumn(
                name: "recipient_email",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "RecipientEmail");

            migrationBuilder.RenameColumn(
                name: "raw_payload",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "RawPayload");

            migrationBuilder.RenameColumn(
                name: "outbox_message_id",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "OutboxMessageId");

            migrationBuilder.RenameColumn(
                name: "event_type",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "EventType");

            migrationBuilder.RenameIndex(
                name: "ix_email_delivery_logs_tenant_id_recorded_at",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "IX_email_delivery_logs_TenantId_RecordedAt");

            migrationBuilder.RenameIndex(
                name: "ix_email_delivery_logs_outbox_message_id",
                schema: "notifications",
                table: "email_delivery_logs",
                newName: "IX_email_delivery_logs_OutboxMessageId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_email_templates",
                schema: "notifications",
                table: "email_templates",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_email_outbox_messages",
                schema: "notifications",
                table: "email_outbox_messages",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_email_delivery_logs",
                schema: "notifications",
                table: "email_delivery_logs",
                column: "Id");
        }
    }
}
