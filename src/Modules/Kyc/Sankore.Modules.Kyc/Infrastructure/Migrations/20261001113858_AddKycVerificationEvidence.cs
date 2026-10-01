using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycVerificationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kyc_confidence_assessments",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    global_score = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    breakdown_json = table.Column<string>(type: "jsonb", nullable: true),
                    flags_json = table.Column<string>(type: "jsonb", nullable: true),
                    service_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_confidence_assessments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_face_verifications",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    selfie_storage_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    similarity_score = table.Column<double>(type: "double precision", nullable: false),
                    is_match = table.Column<bool>(type: "boolean", nullable: false),
                    quality_scores_json = table.Column<string>(type: "jsonb", nullable: true),
                    model_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_face_verifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_field_corrections",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    encrypted_previous_value = table.Column<string>(type: "text", nullable: true),
                    encrypted_new_value = table.Column<string>(type: "text", nullable: false),
                    corrected_by = table.Column<Guid>(type: "uuid", nullable: false),
                    corrected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_field_corrections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_identity_documents",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    encrypted_number = table.Column<string>(type: "text", nullable: false),
                    number_blind_index = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issuing_country = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: true),
                    ocr_fields_json = table.Column<string>(type: "jsonb", nullable: true),
                    mrz_data_json = table.Column<string>(type: "jsonb", nullable: true),
                    storage_ref = table.Column<string>(type: "text", nullable: true),
                    service_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_identity_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_confidence_assessments_file",
                schema: "kyc",
                table: "kyc_confidence_assessments",
                columns: new[] { "kyc_file_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_kyc_face_verifications_file_attempt",
                schema: "kyc",
                table: "kyc_face_verifications",
                columns: new[] { "kyc_file_id", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_kyc_field_corrections_file",
                schema: "kyc",
                table: "kyc_field_corrections",
                columns: new[] { "kyc_file_id", "corrected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_identity_documents_blind_index",
                schema: "kyc",
                table: "kyc_identity_documents",
                columns: new[] { "tenant_id", "number_blind_index" });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_identity_documents_file",
                schema: "kyc",
                table: "kyc_identity_documents",
                column: "kyc_file_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_confidence_assessments",
                schema: "kyc");

            migrationBuilder.DropTable(
                name: "kyc_face_verifications",
                schema: "kyc");

            migrationBuilder.DropTable(
                name: "kyc_field_corrections",
                schema: "kyc");

            migrationBuilder.DropTable(
                name: "kyc_identity_documents",
                schema: "kyc");
        }
    }
}
