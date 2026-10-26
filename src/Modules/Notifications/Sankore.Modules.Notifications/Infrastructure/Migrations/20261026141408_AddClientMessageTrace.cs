using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Notifications.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientMessageTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                schema: "notifications",
                table: "sms_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "content_purged_at",
                schema: "notifications",
                table: "sms_outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                schema: "notifications",
                table: "email_outbox_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "content_purged_at",
                schema: "notifications",
                table: "email_outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sms_outbox_messages_tenant_id_client_id",
                schema: "notifications",
                table: "sms_outbox_messages",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_email_outbox_messages_tenant_id_client_id",
                schema: "notifications",
                table: "email_outbox_messages",
                columns: new[] { "tenant_id", "client_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sms_outbox_messages_tenant_id_client_id",
                schema: "notifications",
                table: "sms_outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_email_outbox_messages_tenant_id_client_id",
                schema: "notifications",
                table: "email_outbox_messages");

            migrationBuilder.DropColumn(
                name: "client_id",
                schema: "notifications",
                table: "sms_outbox_messages");

            migrationBuilder.DropColumn(
                name: "content_purged_at",
                schema: "notifications",
                table: "sms_outbox_messages");

            migrationBuilder.DropColumn(
                name: "client_id",
                schema: "notifications",
                table: "email_outbox_messages");

            migrationBuilder.DropColumn(
                name: "content_purged_at",
                schema: "notifications",
                table: "email_outbox_messages");
        }
    }
}
