using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycDocumentRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "manual_validation_reason",
                schema: "kyc",
                table: "kyc_files",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "manually_validated_at",
                schema: "kyc",
                table: "kyc_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "manually_validated_by",
                schema: "kyc",
                table: "kyc_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "kyc_documents",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    review_decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refusal_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    // xmin is NOT declared here on purpose. PostgreSQL stamps a transaction
                    // id on every row version and exposes it as a system column on every
                    // table, so CREATE TABLE with a column of that name fails outright:
                    //     ERROR: column name "xmin" conflicts with a system column name
                    // KycDocument.Version maps onto it as a concurrency token, which is a
                    // binding, not a schema change. EF scaffolds the column anyway — the
                    // same trap 20261001113109_InitialKyc already documents.
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_documents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_documents_file_kind",
                schema: "kyc",
                table: "kyc_documents",
                columns: new[] { "tenant_id", "kyc_file_id", "kind", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_documents_storage_ref",
                schema: "kyc",
                table: "kyc_documents",
                columns: new[] { "tenant_id", "storage_ref" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_documents",
                schema: "kyc");

            migrationBuilder.DropColumn(
                name: "manual_validation_reason",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "manually_validated_at",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "manually_validated_by",
                schema: "kyc",
                table: "kyc_files");
        }
    }
}
