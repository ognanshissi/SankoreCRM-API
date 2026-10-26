using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceLots3And4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "commission_netting_reference",
                schema: "integration",
                table: "integration_connection",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "has_commission_netting_agreement",
                schema: "integration",
                table: "integration_connection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "client_answer",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_answer_reference",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "client_answered_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "issued_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "medical_exams_requested",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "premium_payer_client_id",
                schema: "integration",
                table: "ins_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sent_to_insurer_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "surcharge_amount",
                schema: "integration",
                table: "ins_subscription",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "surcharge_paid_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "underwriting",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "underwriting_exclusions",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "underwriting_received_on",
                schema: "integration",
                table: "ins_subscription",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "underwriting_recorded_at",
                schema: "integration",
                table: "ins_subscription",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "underwriting_recorded_by",
                schema: "integration",
                table: "ins_subscription",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "underwriting_reference",
                schema: "integration",
                table: "ins_subscription",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "insurer_commission_received_on",
                schema: "integration",
                table: "ins_statement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "insurer_commission_reference",
                schema: "integration",
                table: "ins_statement",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "insurer_declared_commission",
                schema: "integration",
                table: "ins_statement",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "remittance_due_by",
                schema: "integration",
                table: "ins_statement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remittance_reference",
                schema: "integration",
                table: "ins_statement",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "remitted_amount",
                schema: "integration",
                table: "ins_statement",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "remitted_on",
                schema: "integration",
                table: "ins_statement",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_life_insurance",
                schema: "integration",
                table: "ins_product",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ins_policy_document",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    sent_to_client_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sent_channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    sent_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_policy_document", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_policy_document_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_policy_operation",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    refund_due_by = table.Column<DateOnly>(type: "date", nullable: true),
                    payout_instrument_id = table.Column<Guid>(type: "uuid", nullable: true),
                    insurer_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status_changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    paid_on = table.Column<DateOnly>(type: "date", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_policy_operation", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_policy_operation_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_document_policy",
                schema: "integration",
                table: "ins_policy_document",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_document_policy_id",
                schema: "integration",
                table: "ins_policy_document",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_operation_policy",
                schema: "integration",
                table: "ins_policy_operation",
                columns: new[] { "tenant_id", "policy_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_operation_policy_id",
                schema: "integration",
                table: "ins_policy_operation",
                column: "policy_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ins_policy_document",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_policy_operation",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "commission_netting_reference",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "has_commission_netting_agreement",
                schema: "integration",
                table: "integration_connection");

            migrationBuilder.DropColumn(
                name: "client_answer",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "client_answer_reference",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "client_answered_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "issued_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "medical_exams_requested",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "premium_payer_client_id",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "sent_to_insurer_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "surcharge_amount",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "surcharge_paid_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting_exclusions",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting_received_on",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting_recorded_at",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting_recorded_by",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "underwriting_reference",
                schema: "integration",
                table: "ins_subscription");

            migrationBuilder.DropColumn(
                name: "insurer_commission_received_on",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "insurer_commission_reference",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "insurer_declared_commission",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "remittance_due_by",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "remittance_reference",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "remitted_amount",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "remitted_on",
                schema: "integration",
                table: "ins_statement");

            migrationBuilder.DropColumn(
                name: "is_life_insurance",
                schema: "integration",
                table: "ins_product");
        }
    }
}
