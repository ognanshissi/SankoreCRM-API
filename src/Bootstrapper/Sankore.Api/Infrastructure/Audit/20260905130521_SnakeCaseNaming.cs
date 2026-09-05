using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Api.Infrastructure.Audit
{
    /// <inheritdoc />
    public partial class SnakeCaseNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries");

            migrationBuilder.RenameColumn(
                name: "Timestamp",
                schema: "audit",
                table: "entries",
                newName: "timestamp");

            migrationBuilder.RenameColumn(
                name: "Outcome",
                schema: "audit",
                table: "entries",
                newName: "outcome");

            migrationBuilder.RenameColumn(
                name: "Action",
                schema: "audit",
                table: "entries",
                newName: "action");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "audit",
                table: "entries",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "audit",
                table: "entries",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "UserAgent",
                schema: "audit",
                table: "entries",
                newName: "user_agent");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "audit",
                table: "entries",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "ResourceType",
                schema: "audit",
                table: "entries",
                newName: "resource_type");

            migrationBuilder.RenameColumn(
                name: "ResourceId",
                schema: "audit",
                table: "entries",
                newName: "resource_id");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                schema: "audit",
                table: "entries",
                newName: "payload_json");

            migrationBuilder.RenameColumn(
                name: "IpAddress",
                schema: "audit",
                table: "entries",
                newName: "ip_address");

            migrationBuilder.RenameColumn(
                name: "ErrorDetail",
                schema: "audit",
                table: "entries",
                newName: "error_detail");

            migrationBuilder.RenameColumn(
                name: "CorrelationId",
                schema: "audit",
                table: "entries",
                newName: "correlation_id");

            migrationBuilder.RenameIndex(
                name: "IX_entries_TenantId_UserId",
                schema: "audit",
                table: "entries",
                newName: "ix_entries_tenant_id_user_id");

            migrationBuilder.RenameIndex(
                name: "IX_entries_TenantId_Timestamp",
                schema: "audit",
                table: "entries",
                newName: "ix_entries_tenant_id_timestamp");

            migrationBuilder.RenameIndex(
                name: "IX_entries_TenantId_ResourceType_ResourceId",
                schema: "audit",
                table: "entries",
                newName: "ix_entries_tenant_id_resource_type_resource_id");

            migrationBuilder.RenameIndex(
                name: "IX_entries_TenantId_Action",
                schema: "audit",
                table: "entries",
                newName: "ix_entries_tenant_id_action");

            migrationBuilder.AddPrimaryKey(
                name: "pk_entries",
                schema: "audit",
                table: "entries",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_entries",
                schema: "audit",
                table: "entries");

            migrationBuilder.RenameColumn(
                name: "timestamp",
                schema: "audit",
                table: "entries",
                newName: "Timestamp");

            migrationBuilder.RenameColumn(
                name: "outcome",
                schema: "audit",
                table: "entries",
                newName: "Outcome");

            migrationBuilder.RenameColumn(
                name: "action",
                schema: "audit",
                table: "entries",
                newName: "Action");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "audit",
                table: "entries",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "audit",
                table: "entries",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "user_agent",
                schema: "audit",
                table: "entries",
                newName: "UserAgent");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "audit",
                table: "entries",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "resource_type",
                schema: "audit",
                table: "entries",
                newName: "ResourceType");

            migrationBuilder.RenameColumn(
                name: "resource_id",
                schema: "audit",
                table: "entries",
                newName: "ResourceId");

            migrationBuilder.RenameColumn(
                name: "payload_json",
                schema: "audit",
                table: "entries",
                newName: "PayloadJson");

            migrationBuilder.RenameColumn(
                name: "ip_address",
                schema: "audit",
                table: "entries",
                newName: "IpAddress");

            migrationBuilder.RenameColumn(
                name: "error_detail",
                schema: "audit",
                table: "entries",
                newName: "ErrorDetail");

            migrationBuilder.RenameColumn(
                name: "correlation_id",
                schema: "audit",
                table: "entries",
                newName: "CorrelationId");

            migrationBuilder.RenameIndex(
                name: "ix_entries_tenant_id_user_id",
                schema: "audit",
                table: "entries",
                newName: "IX_entries_TenantId_UserId");

            migrationBuilder.RenameIndex(
                name: "ix_entries_tenant_id_timestamp",
                schema: "audit",
                table: "entries",
                newName: "IX_entries_TenantId_Timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_entries_tenant_id_resource_type_resource_id",
                schema: "audit",
                table: "entries",
                newName: "IX_entries_TenantId_ResourceType_ResourceId");

            migrationBuilder.RenameIndex(
                name: "ix_entries_tenant_id_action",
                schema: "audit",
                table: "entries",
                newName: "IX_entries_TenantId_Action");

            migrationBuilder.AddPrimaryKey(
                name: "PK_entries",
                schema: "audit",
                table: "entries",
                column: "Id");
        }
    }
}
