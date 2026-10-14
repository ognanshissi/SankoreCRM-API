using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSmsSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_sms_settings",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sender_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    account_sid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    default_calling_code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    has_credential = table.Column<bool>(type: "boolean", nullable: false),
                    monthly_quota_limit = table.Column<int>(type: "integer", nullable: true),
                    current_month_usage_count = table.Column<int>(type: "integer", nullable: false),
                    current_month_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    // NO xmin column, deliberately. EF scaffolds one because the entity maps a
                    // concurrency token onto it, and that DDL FAILS on PostgreSQL:
                    //     ERROR: column name "xmin" conflicts with a system column name
                    // xmin exists on every table already — it is the transaction id Postgres
                    // stamps on each row version — so mapping it is a read-only binding and not a
                    // schema change. AddNotificationSettingsConcurrency made the same deletion for
                    // the email row, and XminIsNeverScaffoldedTests pins it across every module.
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_sms_settings", x => x.id);
                });

            // One row per tenant.
            migrationBuilder.CreateIndex(
                name: "ix_tenant_sms_settings_tenant_id",
                schema: "administration",
                table: "tenant_sms_settings",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_sms_settings",
                schema: "administration");
        }
    }
}
