using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_notification_settings",
                schema: "administration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UseDefaultPlatformProvider = table.Column<bool>(type: "boolean", nullable: false),
                    FromEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FromName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReplyToEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SendingDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    CredentialVaultPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MonthlyQuotaLimit = table.Column<int>(type: "integer", nullable: true),
                    CurrentMonthUsageCount = table.Column<int>(type: "integer", nullable: false),
                    CurrentMonthStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_notification_settings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_notification_settings_TenantId",
                schema: "administration",
                table: "tenant_notification_settings",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_notification_settings",
                schema: "administration");
        }
    }
}
