using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycDocumentAccessLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kyc_document_access_logs",
                schema: "kyc",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kyc_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kyc_document_access_logs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_document_access_logs_actor",
                schema: "kyc",
                table: "kyc_document_access_logs",
                columns: new[] { "tenant_id", "actor_user_id", "accessed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kyc_document_access_logs_file",
                schema: "kyc",
                table: "kyc_document_access_logs",
                columns: new[] { "tenant_id", "kyc_file_id", "accessed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kyc_document_access_logs",
                schema: "kyc");
        }
    }
}
