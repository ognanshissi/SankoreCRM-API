using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycRemoteInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "product_line_id",
                schema: "kyc",
                table: "kyc_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "responsible_agent_id",
                schema: "kyc",
                table: "kyc_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "kyc_remote_invitations",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    identified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quality_retries = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    consent_given_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consent_text_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    // The scaffolded `xmin` column is deliberately DELETED. PostgreSQL rejects a
                    // table that names it — 'column name "xmin" conflicts with a system column
                    // name' — which aborts MigrateAsync and leaves the table absent altogether.
                    // The optimistic-concurrency token maps onto the system column and belongs to
                    // the model snapshot, not to the DDL. XminIsNeverScaffoldedTests pins this.
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_remote_invitations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_remote_invitations_expiry",
                schema: "kyc",
                table: "kyc_remote_invitations",
                columns: new[] { "tenant_id", "expires_at" },
                filter: "status IN ('Scheduled', 'Sent', 'Opened', 'Identified')");

            migrationBuilder.CreateIndex(
                name: "ix_kyc_remote_invitations_file_created",
                schema: "kyc",
                table: "kyc_remote_invitations",
                columns: new[] { "tenant_id", "kyc_file_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_kyc_remote_invitations_active_per_file",
                schema: "kyc",
                table: "kyc_remote_invitations",
                columns: new[] { "tenant_id", "kyc_file_id" },
                unique: true,
                filter: "status IN ('Scheduled', 'Sent', 'Opened', 'Identified')");

            migrationBuilder.CreateIndex(
                name: "ux_kyc_remote_invitations_token",
                schema: "kyc",
                table: "kyc_remote_invitations",
                column: "token_hash",
                unique: true,
                filter: "token_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_remote_invitations",
                schema: "kyc");

            migrationBuilder.DropColumn(
                name: "product_line_id",
                schema: "kyc",
                table: "kyc_files");

            migrationBuilder.DropColumn(
                name: "responsible_agent_id",
                schema: "kyc",
                table: "kyc_files");
        }
    }
}
