using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailSignatureAndScheduledSend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mail_signature",
                schema: "integration",
                table: "cal_connected_account",
                type: "character varying(10000)",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cal_mail_scheduled",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    send_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_enc = table.Column<string>(type: "text", nullable: false),
                    last_error = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_scheduled", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cal_mail_scheduled_attachment",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_mail_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    content_enc = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_scheduled_attachment", x => x.id);
                    table.ForeignKey(
                        name: "fk_cal_mail_scheduled_attachment_scheduled_mails_scheduled_mai",
                        column: x => x.scheduled_mail_id,
                        principalSchema: "integration",
                        principalTable: "cal_mail_scheduled",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_scheduled_tenant_id_account_id",
                schema: "integration",
                table: "cal_mail_scheduled",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_scheduled_tenant_id_user_id_send_at",
                schema: "integration",
                table: "cal_mail_scheduled",
                columns: new[] { "tenant_id", "user_id", "send_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_scheduled_attachment_scheduled_mail_id",
                schema: "integration",
                table: "cal_mail_scheduled_attachment",
                column: "scheduled_mail_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_mail_scheduled_attachment",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "cal_mail_scheduled",
                schema: "integration");

            migrationBuilder.DropColumn(
                name: "mail_signature",
                schema: "integration",
                table: "cal_connected_account");
        }
    }
}
