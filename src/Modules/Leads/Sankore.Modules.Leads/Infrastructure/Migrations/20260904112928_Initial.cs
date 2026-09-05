using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "leads");

            migrationBuilder.CreateTable(
                name: "dispatching_rules",
                schema: "leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Strategy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    weight_language = table.Column<double>(type: "double precision", nullable: false),
                    weight_product = table.Column<double>(type: "double precision", nullable: false),
                    weight_geography = table.Column<double>(type: "double precision", nullable: false),
                    weight_workload = table.Column<double>(type: "double precision", nullable: false),
                    weight_performance = table.Column<double>(type: "double precision", nullable: false),
                    MaxLeadsPerAgent = table.Column<int>(type: "integer", nullable: false),
                    AntiMonopolyThreshold = table.Column<int>(type: "integer", nullable: false),
                    FirstContactSla = table.Column<TimeSpan>(type: "interval", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dispatching_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "leads",
                schema: "leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Website = table.Column<string>(type: "text", nullable: false),
                    CompanyName = table.Column<string>(type: "text", nullable: false),
                    CompanyEmail = table.Column<string>(type: "text", nullable: false),
                    CompanyPhone = table.Column<string>(type: "text", nullable: false),
                    CompanyAddress = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InterestedProduct = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    lat = table.Column<double>(type: "double precision", nullable: true),
                    lng = table.Column<double>(type: "double precision", nullable: true),
                    PreferredAgencyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentAssignedId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentCollectedLeadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    CurrentAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    LossReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "leads",
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

            migrationBuilder.CreateTable(
                name: "lead_assignments",
                schema: "leads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Strategy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CompatibilityScore = table.Column<double>(type: "double precision", nullable: false),
                    WasManualOverride = table.Column<bool>(type: "boolean", nullable: false),
                    OverrideReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SlaDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstContactAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lead_assignments_leads_LeadId",
                        column: x => x.LeadId,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dispatching_rules_TenantId_IsActive",
                schema: "leads",
                table: "dispatching_rules",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_AgentId",
                schema: "leads",
                table: "lead_assignments",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_LeadId",
                schema: "leads",
                table: "lead_assignments",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_SlaDeadline_FirstContactAt",
                schema: "leads",
                table: "lead_assignments",
                columns: new[] { "SlaDeadline", "FirstContactAt" });

            migrationBuilder.CreateIndex(
                name: "IX_leads_FullName",
                schema: "leads",
                table: "leads",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_leads_PhoneNumber",
                schema: "leads",
                table: "leads",
                column: "PhoneNumber");

            migrationBuilder.CreateIndex(
                name: "IX_leads_TenantId_Status",
                schema: "leads",
                table: "leads",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                schema: "leads",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dispatching_rules",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "lead_assignments",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "leads",
                schema: "leads");
        }
    }
}
