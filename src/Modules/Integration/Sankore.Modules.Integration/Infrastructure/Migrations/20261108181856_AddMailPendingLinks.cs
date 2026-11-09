using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailPendingLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cal_mail_pending_link",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    internet_message_id = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    entity_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cal_mail_pending_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_cal_mail_pending_link_cal_connected_account_account_id",
                        column: x => x.account_id,
                        principalSchema: "integration",
                        principalTable: "cal_connected_account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_pending_link_account_id",
                schema: "integration",
                table: "cal_mail_pending_link",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_cal_mail_pending_link_tenant_id_account_id",
                schema: "integration",
                table: "cal_mail_pending_link",
                columns: new[] { "tenant_id", "account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cal_mail_pending_link",
                schema: "integration");
        }
    }
}
