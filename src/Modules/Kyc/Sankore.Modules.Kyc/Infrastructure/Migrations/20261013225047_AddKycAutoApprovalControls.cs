using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycAutoApprovalControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kyc_auto_approval_controls",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    granted_tier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    policy_version = table.Column<int>(type: "integer", nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finding = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    raised_review_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_auto_approval_controls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_auto_approval_controls_file",
                schema: "kyc",
                table: "kyc_auto_approval_controls",
                column: "kyc_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_kyc_auto_approval_controls_queue",
                schema: "kyc",
                table: "kyc_auto_approval_controls",
                columns: new[] { "tenant_id", "status", "sampled_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_auto_approval_controls",
                schema: "kyc");
        }
    }
}
