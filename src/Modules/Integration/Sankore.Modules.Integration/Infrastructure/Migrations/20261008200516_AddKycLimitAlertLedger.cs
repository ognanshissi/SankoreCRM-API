using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycLimitAlertLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integration_kyc_limit_alert",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    limit_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    observed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ceiling = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_kyc_limit_alert", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_kyc_limit_alert_tenant_raised",
                schema: "integration",
                table: "integration_kyc_limit_alert",
                columns: new[] { "tenant_id", "raised_at" });

            migrationBuilder.CreateIndex(
                name: "ux_integration_kyc_limit_alert_period",
                schema: "integration",
                table: "integration_kyc_limit_alert",
                columns: new[] { "tenant_id", "crm_customer_id", "limit_kind", "severity", "period" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_kyc_limit_alert",
                schema: "integration");
        }
    }
}
