using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "client_export_jobs",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    filters_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    file_reference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    download_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_export_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_groups",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    constitution_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    dissolution_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    dissolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_loyalty_scores",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    is_provisional = table.Column<bool>(type: "boolean", nullable: false),
                    breakdown_json = table.Column<string>(type: "jsonb", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_loyalty_scores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_number_sequences",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_value = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_number_sequences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_segment_history",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    segment_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rule_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_segment_history", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "client_timeline_entries",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_module = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entry_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    reference_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    reference_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    dedup_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_timeline_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clients",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    advisor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    search_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    first_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    last_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    maiden_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    gender = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    encrypted_date_of_birth = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    date_of_birth_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    birth_place = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    nationality = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    marital_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    father_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    mother_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    profession = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    employer = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    encrypted_declared_income = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    declared_income_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    preferred_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    dependents_count = table.Column<int>(type: "integer", nullable: false),
                    identity_document_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    encrypted_identity_document_number = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    identity_document_number_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    identity_document_issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    identity_document_expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    legal_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    legal_form_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    encrypted_registration_number = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    registration_number_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    encrypted_tax_id_number = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    incorporation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    phonetic_key_primary = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    phonetic_key_secondary = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    kyc_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kyc_rejection_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    risk_level = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    kyc_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    segment_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    segment_assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    loyalty_score = table.Column<int>(type: "integer", nullable: true),
                    loyalty_score_provisional = table.Column<bool>(type: "boolean", nullable: false),
                    loyalty_score_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merged_into_id = table.Column<Guid>(type: "uuid", nullable: true),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archive_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_anonymized = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "customer_settings",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "legal_forms",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_forms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sensitive_data_access_logs",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sensitive_data_access_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "beneficial_owners",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    external_nationality = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    encrypted_external_document_number = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    external_document_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ownership_percentage = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    control_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_beneficial_owners", x => x.id);
                    table.ForeignKey(
                        name: "fk_beneficial_owners_clients_linked_client_id",
                        column: x => x.linked_client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_contact_points",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    encrypted_value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_contact_points", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_contact_points_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "client_merge_requests",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    survivor_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    absorbed_client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_choices_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    workflow_instance_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_merge_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_merge_requests_clients_absorbed_client_id",
                        column: x => x.absorbed_client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_client_merge_requests_clients_survivor_client_id",
                        column: x => x.survivor_client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_relationships",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    related_client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    external_full_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    external_phone_encrypted = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    external_phone_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    external_date_of_birth = table.Column<DateOnly>(type: "date", nullable: true),
                    encrypted_external_document_number = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reciprocal_relationship_id = table.Column<Guid>(type: "uuid", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_relationships", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_relationships_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_client_relationships_clients_related_client_id",
                        column: x => x.related_client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "client_status_history",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    old_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    new_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_status_history_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "duplicate_candidates",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_a_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_b_id = table.Column<Guid>(type: "uuid", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    reasons_json = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    fingerprint_a = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fingerprint_b = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_duplicate_candidates", x => x.id);
                    table.ForeignKey(
                        name: "fk_duplicate_candidates_clients_client_a_id",
                        column: x => x.client_a_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_duplicate_candidates_clients_client_b_id",
                        column: x => x.client_b_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_memberships",
                schema: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    office_role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    leave_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_group_memberships_client_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "customers",
                        principalTable: "client_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_memberships_clients_client_id",
                        column: x => x.client_id,
                        principalSchema: "customers",
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_beneficial_owners_linked_client_id",
                schema: "customers",
                table: "beneficial_owners",
                column: "linked_client_id");

            migrationBuilder.CreateIndex(
                name: "ix_beneficial_owners_tenant_id_legal_client_id",
                schema: "customers",
                table: "beneficial_owners",
                columns: new[] { "tenant_id", "legal_client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_beneficial_owners_tenant_id_linked_client_id",
                schema: "customers",
                table: "beneficial_owners",
                columns: new[] { "tenant_id", "linked_client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_contact_points_client_id",
                schema: "customers",
                table: "client_contact_points",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_contact_points_tenant_id_type_blind_index",
                schema: "customers",
                table: "client_contact_points",
                columns: new[] { "tenant_id", "type", "blind_index" });

            migrationBuilder.CreateIndex(
                name: "ux_client_contact_points_primary",
                schema: "customers",
                table: "client_contact_points",
                columns: new[] { "tenant_id", "client_id", "type" },
                unique: true,
                filter: "is_primary = true AND valid_to IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_client_export_jobs_tenant_id_requested_by_requested_at",
                schema: "customers",
                table: "client_export_jobs",
                columns: new[] { "tenant_id", "requested_by", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_client_export_jobs_token",
                schema: "customers",
                table: "client_export_jobs",
                columns: new[] { "tenant_id", "download_token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_client_groups_name",
                schema: "customers",
                table: "client_groups",
                columns: new[] { "tenant_id", "agency_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_client_loyalty_scores_tenant_id_client_id",
                schema: "customers",
                table: "client_loyalty_scores",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_loyalty_scores_tenant_id_client_id_computed_at",
                schema: "customers",
                table: "client_loyalty_scores",
                columns: new[] { "tenant_id", "client_id", "computed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_client_merge_requests_absorbed_client_id",
                schema: "customers",
                table: "client_merge_requests",
                column: "absorbed_client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_merge_requests_survivor_client_id",
                schema: "customers",
                table: "client_merge_requests",
                column: "survivor_client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_merge_requests_tenant_id_status",
                schema: "customers",
                table: "client_merge_requests",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_client_merge_requests_tenant_id_workflow_instance_id",
                schema: "customers",
                table: "client_merge_requests",
                columns: new[] { "tenant_id", "workflow_instance_id" });

            migrationBuilder.CreateIndex(
                name: "ux_client_number_sequences",
                schema: "customers",
                table: "client_number_sequences",
                columns: new[] { "tenant_id", "agency_code", "year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_client_relationships_client_id",
                schema: "customers",
                table: "client_relationships",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_relationships_related_client_id",
                schema: "customers",
                table: "client_relationships",
                column: "related_client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_relationships_tenant_id_client_id",
                schema: "customers",
                table: "client_relationships",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_relationships_tenant_id_client_id_type",
                schema: "customers",
                table: "client_relationships",
                columns: new[] { "tenant_id", "client_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_client_relationships_tenant_id_related_client_id",
                schema: "customers",
                table: "client_relationships",
                columns: new[] { "tenant_id", "related_client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_segment_history_tenant_id_client_id",
                schema: "customers",
                table: "client_segment_history",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_segment_history_tenant_id_client_id_valid_from",
                schema: "customers",
                table: "client_segment_history",
                columns: new[] { "tenant_id", "client_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ix_client_status_history_client_id",
                schema: "customers",
                table: "client_status_history",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_status_history_tenant_id_client_id",
                schema: "customers",
                table: "client_status_history",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_client_status_history_tenant_id_client_id_occurred_at",
                schema: "customers",
                table: "client_status_history",
                columns: new[] { "tenant_id", "client_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_client_timeline_entries_tenant_id_client_id_occurred_at",
                schema: "customers",
                table: "client_timeline_entries",
                columns: new[] { "tenant_id", "client_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ux_client_timeline_dedup",
                schema: "customers",
                table: "client_timeline_entries",
                columns: new[] { "tenant_id", "dedup_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_advisor_user_id",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "advisor_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_agency_id",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "agency_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_date_of_birth_blind_index",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "date_of_birth_blind_index" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_display_name",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "display_name" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_first_name",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "first_name" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_last_name",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "last_name" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_legal_name",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "legal_name" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_merged_into_id",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "merged_into_id" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_phonetic_key_primary",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "phonetic_key_primary" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_search_key",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "search_key" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_segment_code",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "segment_code" });

            migrationBuilder.CreateIndex(
                name: "ix_clients_tenant_id_status",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_clients_identity_doc",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "identity_document_number_blind_index" },
                unique: true,
                filter: "identity_document_number_blind_index IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_clients_number",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "client_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_clients_registration",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "registration_number_blind_index" },
                unique: true,
                filter: "registration_number_blind_index IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_clients_source_lead",
                schema: "customers",
                table: "clients",
                columns: new[] { "tenant_id", "source_lead_id" },
                unique: true,
                filter: "source_lead_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_customer_settings_key",
                schema: "customers",
                table: "customer_settings",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_duplicate_candidates_client_a_id",
                schema: "customers",
                table: "duplicate_candidates",
                column: "client_a_id");

            migrationBuilder.CreateIndex(
                name: "ix_duplicate_candidates_client_b_id",
                schema: "customers",
                table: "duplicate_candidates",
                column: "client_b_id");

            migrationBuilder.CreateIndex(
                name: "ix_duplicate_candidates_tenant_id_status",
                schema: "customers",
                table: "duplicate_candidates",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_duplicate_candidates_pair",
                schema: "customers",
                table: "duplicate_candidates",
                columns: new[] { "tenant_id", "client_a_id", "client_b_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_memberships_client_id",
                schema: "customers",
                table: "group_memberships",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_memberships_group_id",
                schema: "customers",
                table: "group_memberships",
                column: "group_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_memberships_tenant_id_client_id",
                schema: "customers",
                table: "group_memberships",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_group_memberships_tenant_id_group_id",
                schema: "customers",
                table: "group_memberships",
                columns: new[] { "tenant_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ux_group_memberships_active",
                schema: "customers",
                table: "group_memberships",
                columns: new[] { "tenant_id", "group_id", "client_id" },
                unique: true,
                filter: "left_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_inbox_messages_tenant_id_received_at",
                schema: "customers",
                table: "inbox_messages",
                columns: new[] { "tenant_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_legal_forms_code",
                schema: "customers",
                table: "legal_forms",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "customers",
                table: "outbox_messages",
                columns: new[] { "processed_at", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_data_access_logs_tenant_id_actor_user_id_accessed",
                schema: "customers",
                table: "sensitive_data_access_logs",
                columns: new[] { "tenant_id", "actor_user_id", "accessed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sensitive_data_access_logs_tenant_id_client_id",
                schema: "customers",
                table: "sensitive_data_access_logs",
                columns: new[] { "tenant_id", "client_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "beneficial_owners",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_contact_points",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_export_jobs",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_loyalty_scores",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_merge_requests",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_number_sequences",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_relationships",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_segment_history",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_status_history",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_timeline_entries",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "customer_settings",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "duplicate_candidates",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "group_memberships",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "legal_forms",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "sensitive_data_access_logs",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "client_groups",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "clients",
                schema: "customers");
        }
    }
}
