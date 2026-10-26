using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceSubscriptionFromHolding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.AddColumn<string>(
                name: "answers_sha256",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "clause_version",
                schema: "integration",
                table: "ins_subscription",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<Guid>(
                name: "crm_holding_id",
                schema: "integration",
                table: "ins_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_mode",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "CoreBankingDebit");

            migrationBuilder.AddColumn<DateOnly>(
                name: "recorded_paid_on",
                schema: "integration",
                table: "ins_subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recorded_payment_method",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recorded_payment_reference",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_opportunity_id",
                schema: "integration",
                table: "ins_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_holding_id", "clause_version" },
                unique: true,
                filter: "crm_holding_id IS NOT NULL AND status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue')");

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_customer_id", "product_id", "effective_date" },
                unique: true,
                filter: "status NOT IN ('DebitFailed', 'Reversed', 'Cancelled', 'PremiumRefundDue')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_holding",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "answers_sha256",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "clause_version",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "crm_holding_id",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "payment_mode",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "recorded_paid_on",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "recorded_payment_method",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "recorded_payment_reference",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "source_opportunity_id",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_customer_id", "product_id", "effective_date" },
                unique: true);
        }
    }
}
