using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Admin.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSignups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Offer",
                table: "Tenants",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Paid");

            migrationBuilder.CreateTable(
                name: "TenantSignups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Slug = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    LastName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Email = table.Column<string>(type: "citext", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ActivityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RequestIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByAdminId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSignups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_signups_email",
                table: "TenantSignups",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_signups_request_ip",
                table: "TenantSignups",
                column: "RequestIp");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_signups_slug",
                table: "TenantSignups",
                column: "Slug");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_signups_status_created",
                table: "TenantSignups",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_signups_tenant",
                table: "TenantSignups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "ux_tenant_signups_token_hash",
                table: "TenantSignups",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantSignups");

            migrationBuilder.DropColumn(
                name: "Offer",
                table: "Tenants");
        }
    }
}
