using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialKyc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "kyc");

            migrationBuilder.CreateTable(
                name: "kyc_files",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    vigilance_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence_score = table.Column<int>(type: "integer", nullable: true),
                    confidence_level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    face_match_attempts = table.Column<int>(type: "integer", nullable: false),
                    duplicate_suspected = table.Column<bool>(type: "boolean", nullable: false),
                    next_review_date = table.Column<DateOnly>(type: "date", nullable: true),
                    validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    last_submitted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    // xmin is NOT declared here on purpose. PostgreSQL stamps a transaction
                    // id on every row version and exposes it as a system column on every
                    // table, so CREATE TABLE with a column of that name fails outright:
                    //     ERROR: column name "xmin" conflicts with a system column name
                    // KycFile.Version maps onto it as a concurrency token, which is a
                    // binding, not a schema change. EF scaffolds the column anyway — the
                    // same scaffold already made the Administration, Leads and Customers
                    // migrations uncreatable before it was caught.
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "kyc_settings",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    value_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "kyc",
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

            migrationBuilder.CreateIndex(
                name: "ix_kyc_files_next_review",
                schema: "kyc",
                table: "kyc_files",
                columns: new[] { "tenant_id", "next_review_date" });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_files_status",
                schema: "kyc",
                table: "kyc_files",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_kyc_files_open_per_customer",
                schema: "kyc",
                table: "kyc_files",
                columns: new[] { "tenant_id", "customer_id" },
                unique: true,
                filter: "status NOT IN ('Rejected', 'Suspended')");

            migrationBuilder.CreateIndex(
                name: "ux_kyc_settings_tenant_key",
                schema: "kyc",
                table: "kyc_settings",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "kyc",
                table: "outbox_messages",
                columns: new[] { "processed_at", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_files",
                schema: "kyc");

            migrationBuilder.DropTable(
                name: "kyc_settings",
                schema: "kyc");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "kyc");
        }
    }
}
