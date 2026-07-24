using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Users.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "users");

            migrationBuilder.CreateTable(
                name: "app_users",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    SpokenLanguages = table.Column<List<string>>(type: "text[]", nullable: false),
                    Specialties = table.Column<List<string>>(type: "text[]", nullable: false),
                    last_lat = table.Column<double>(type: "double precision", nullable: true),
                    last_lng = table.Column<double>(type: "double precision", nullable: true),
                    ActiveLeadsCount = table.Column<int>(type: "integer", nullable: false),
                    HotLeadsCount = table.Column<int>(type: "integer", nullable: false),
                    ConversionRate30d = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_users_TenantId_AgencyId",
                schema: "users",
                table: "app_users",
                columns: new[] { "TenantId", "AgencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                schema: "users",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_users",
                schema: "users");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "users");
        }
    }
}
