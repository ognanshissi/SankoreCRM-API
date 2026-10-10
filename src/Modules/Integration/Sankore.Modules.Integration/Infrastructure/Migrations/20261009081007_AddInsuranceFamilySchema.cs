using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInsuranceFamilySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ins_product",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insurer_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    guarantees = table.Column<string>(type: "jsonb", nullable: false),
                    periodicity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    pricing_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fixed_premium_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    insured_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    min_age = table.Column<int>(type: "integer", nullable: true),
                    max_age = table.Column<int>(type: "integer", nullable: true),
                    min_kyc_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requires_cbs_account = table.Column<bool>(type: "boolean", nullable: false),
                    requires_active_loan = table.Column<bool>(type: "boolean", nullable: false),
                    linked_credit_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    commission_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    premium_retry_limit = table.Column<int>(type: "integer", nullable: false),
                    premium_retry_interval_days = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    effective_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_product", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_product_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_statement",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subscription_count = table.Column<int>(type: "integer", nullable: false),
                    cancellation_count = table.Column<int>(type: "integer", nullable: false),
                    reversal_count = table.Column<int>(type: "integer", nullable: false),
                    premium_collected = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    premium_reversed = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    commission_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finalised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finalised_by = table.Column<Guid>(type: "uuid", nullable: true),
                    csv_storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    pdf_storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    transmit_batch_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transmitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    transmit_failure_detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_statement", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_statement_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_policy",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_policy_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    policy_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    insurer_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    next_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    premium_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    periodicity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    insured_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_policy", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_policy_ins_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "integration",
                        principalTable: "ins_product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ins_policy_integration_connection_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_claim",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    nature = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description_encrypted = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    missing_documents = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    external_claim_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    claim_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    declare_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    declared_by = table.Column<Guid>(type: "uuid", nullable: false),
                    indemnity_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    indemnity_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    indemnity_cbs_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    indemnity_paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_claim", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_claim_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ins_claim_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_policy_certificate",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    insurer_certificate_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_policy_certificate", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_policy_certificate_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_premium_instalment",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_no = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    debit_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cbs_debit_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    last_error_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    unpaid_declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unpaid_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_premium_instalment", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_premium_instalment_ins_policy_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_statement_line",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instalment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    commission_rate = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    commission_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    policy_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    insurer_product_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    cbs_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_statement_line", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_statement_line_ins_statement_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "integration",
                        principalTable: "ins_statement",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ins_statement_line_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_subscription",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    premium_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cbs_account_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cbs_debit_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    debited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cbs_reversal_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversal_attempts = table.Column<int>(type: "integer", nullable: false),
                    debit_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscribe_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversal_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    insurer_policy_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    beneficiaries_encrypted = table.Column<string>(type: "text", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    failure_detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_subscription", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_subscription_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "integration",
                        principalTable: "integration_connection",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ins_subscription_ins_product_product_id",
                        column: x => x.product_id,
                        principalSchema: "integration",
                        principalTable: "ins_product",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ins_subscription_policies_policy_id",
                        column: x => x.policy_id,
                        principalSchema: "integration",
                        principalTable: "ins_policy",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_claim_document",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    scan_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scan_detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    requested_by_insurer = table.Column<bool>(type: "boolean", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    transmitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    transmit_command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_claim_document", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_claim_document_claims_claim_id",
                        column: x => x.claim_id,
                        principalSchema: "integration",
                        principalTable: "ins_claim",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_consent_proof",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: true),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    consent_text_version = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    consent_text_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    consented_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_encrypted = table.Column<string>(type: "text", nullable: true),
                    evidence_storage_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    evidence_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    captured_by = table.Column<Guid>(type: "uuid", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_consent_proof", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_consent_proof_insurance_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "integration",
                        principalTable: "ins_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ins_medical_questionnaire",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crm_customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    questionnaire_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    answers_encrypted = table.Column<string>(type: "text", nullable: false),
                    requires_medical_underwriting = table.Column<bool>(type: "boolean", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    captured_by = table.Column<Guid>(type: "uuid", nullable: false),
                    retain_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ins_medical_questionnaire", x => x.id);
                    table.ForeignKey(
                        name: "fk_ins_medical_questionnaire_ins_subscription_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "integration",
                        principalTable: "ins_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_connection_id",
                schema: "integration",
                table: "ins_claim",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_customer_declared",
                schema: "integration",
                table: "ins_claim",
                columns: new[] { "tenant_id", "crm_customer_id", "declared_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_declare_command",
                schema: "integration",
                table: "ins_claim",
                column: "declare_command_id",
                filter: "declare_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_policy_id",
                schema: "integration",
                table: "ins_claim",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_tenant_status",
                schema: "integration",
                table: "ins_claim",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_claim_declaration",
                schema: "integration",
                table: "ins_claim",
                columns: new[] { "tenant_id", "policy_id", "occurred_on", "nature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ins_claim_external",
                schema: "integration",
                table: "ins_claim",
                columns: new[] { "tenant_id", "connection_id", "external_claim_id" },
                unique: true,
                filter: "external_claim_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_document_claim_id",
                schema: "integration",
                table: "ins_claim_document",
                column: "claim_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_claim_document_scan_status",
                schema: "integration",
                table: "ins_claim_document",
                columns: new[] { "tenant_id", "scan_status" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_claim_document_digest",
                schema: "integration",
                table: "ins_claim_document",
                columns: new[] { "tenant_id", "claim_id", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_consent_proof_command",
                schema: "integration",
                table: "ins_consent_proof",
                column: "command_id",
                filter: "command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_consent_proof_customer",
                schema: "integration",
                table: "ins_consent_proof",
                columns: new[] { "tenant_id", "crm_customer_id", "consented_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_consent_proof_subscription_id",
                schema: "integration",
                table: "ins_consent_proof",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_ins_consent_proof_subscription",
                schema: "integration",
                table: "ins_consent_proof",
                columns: new[] { "tenant_id", "subscription_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_medical_questionnaire_subscription_id",
                schema: "integration",
                table: "ins_medical_questionnaire",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_ins_medical_questionnaire_subscription",
                schema: "integration",
                table: "ins_medical_questionnaire",
                columns: new[] { "tenant_id", "subscription_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_connection_id",
                schema: "integration",
                table: "ins_policy",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_connection_status_changed",
                schema: "integration",
                table: "ins_policy",
                columns: new[] { "tenant_id", "connection_id", "status_changed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_customer_status",
                schema: "integration",
                table: "ins_policy",
                columns: new[] { "tenant_id", "crm_customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_next_due",
                schema: "integration",
                table: "ins_policy",
                columns: new[] { "tenant_id", "next_due_date" },
                filter: "next_due_date IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_product_id",
                schema: "integration",
                table: "ins_policy",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_subscription",
                schema: "integration",
                table: "ins_policy",
                column: "subscription_id",
                filter: "subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ins_policy_external",
                schema: "integration",
                table: "ins_policy",
                columns: new[] { "tenant_id", "connection_id", "external_policy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_certificate_policy_fetched",
                schema: "integration",
                table: "ins_policy_certificate",
                columns: new[] { "tenant_id", "policy_id", "fetched_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_policy_certificate_policy_id",
                schema: "integration",
                table: "ins_policy_certificate",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ux_ins_policy_certificate_digest",
                schema: "integration",
                table: "ins_policy_certificate",
                columns: new[] { "tenant_id", "policy_id", "sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_premium_instalment_debit_command",
                schema: "integration",
                table: "ins_premium_instalment",
                column: "debit_command_id",
                filter: "debit_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_premium_instalment_due_attempt",
                schema: "integration",
                table: "ins_premium_instalment",
                columns: new[] { "status", "due_date", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_premium_instalment_policy_id",
                schema: "integration",
                table: "ins_premium_instalment",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_premium_instalment_tenant_status",
                schema: "integration",
                table: "ins_premium_instalment",
                columns: new[] { "tenant_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_premium_instalment_due",
                schema: "integration",
                table: "ins_premium_instalment",
                columns: new[] { "tenant_id", "policy_id", "due_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_product_connection_id",
                schema: "integration",
                table: "ins_product",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_product_linked_credit",
                schema: "integration",
                table: "ins_product",
                columns: new[] { "tenant_id", "linked_credit_product_code" },
                filter: "linked_credit_product_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_product_tenant_connection_active",
                schema: "integration",
                table: "ins_product",
                columns: new[] { "tenant_id", "connection_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_product_insurer_code",
                schema: "integration",
                table: "ins_product",
                columns: new[] { "tenant_id", "connection_id", "insurer_product_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_statement_connection_id",
                schema: "integration",
                table: "ins_statement",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_statement_tenant_period",
                schema: "integration",
                table: "ins_statement",
                columns: new[] { "tenant_id", "period" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_statement_period",
                schema: "integration",
                table: "ins_statement",
                columns: new[] { "tenant_id", "connection_id", "period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_statement_line_policy_id",
                schema: "integration",
                table: "ins_statement_line",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_statement_line_statement_id",
                schema: "integration",
                table: "ins_statement_line",
                column: "statement_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_statement_line_statement_occurred",
                schema: "integration",
                table: "ins_statement_line",
                columns: new[] { "tenant_id", "statement_id", "occurred_on" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_statement_line",
                schema: "integration",
                table: "ins_statement_line",
                columns: new[] { "tenant_id", "statement_id", "line_type", "policy_id", "instalment_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_connection_id",
                schema: "integration",
                table: "ins_subscription",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_debit_command",
                schema: "integration",
                table: "ins_subscription",
                column: "debit_command_id",
                filter: "debit_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_policy_id",
                schema: "integration",
                table: "ins_subscription",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_product_id",
                schema: "integration",
                table: "ins_subscription",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_reversal_command",
                schema: "integration",
                table: "ins_subscription",
                column: "reversal_command_id",
                filter: "reversal_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_subscribe_command",
                schema: "integration",
                table: "ins_subscription",
                column: "subscribe_command_id",
                filter: "subscribe_command_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_tenant_connection_created",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ins_subscription_tenant_status",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_ins_subscription_idempotency",
                schema: "integration",
                table: "ins_subscription",
                columns: new[] { "tenant_id", "crm_customer_id", "product_id", "effective_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ins_claim_document",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_consent_proof",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_medical_questionnaire",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_policy_certificate",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_premium_instalment",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_statement_line",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_claim",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_subscription",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_statement",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_policy",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "ins_product",
                schema: "integration");
        }
    }
}
