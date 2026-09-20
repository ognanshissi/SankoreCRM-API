using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigurationEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_source_configs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_source_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "pipeline_stage_configs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    is_final = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pipeline_stage_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scoring_configs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    qualification_threshold = table.Column<int>(type: "integer", nullable: false),
                    weight_demographics = table.Column<double>(type: "double precision", nullable: false),
                    weight_engagement = table.Column<double>(type: "double precision", nullable: false),
                    weight_product = table.Column<double>(type: "double precision", nullable: false),
                    weight_channel = table.Column<double>(type: "double precision", nullable: false),
                    weight_recency = table.Column<double>(type: "double precision", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scoring_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sla_configs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    first_contact_deadline = table.Column<TimeSpan>(type: "interval", nullable: false),
                    qualification_deadline = table.Column<TimeSpan>(type: "interval", nullable: false),
                    follow_up_deadline = table.Column<TimeSpan>(type: "interval", nullable: false),
                    escalation_deadline = table.Column<TimeSpan>(type: "interval", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_configs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_type_configs",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_type_configs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_source_configs_tenant_id_code",
                schema: "leads",
                table: "lead_source_configs",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pipeline_stage_configs_tenant_id_code",
                schema: "leads",
                table: "pipeline_stage_configs",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scoring_configs_tenant_id_is_active",
                schema: "leads",
                table: "scoring_configs",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_configs_tenant_id_agency_id_is_active",
                schema: "leads",
                table: "sla_configs",
                columns: new[] { "tenant_id", "agency_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_task_type_configs_tenant_id_code",
                schema: "leads",
                table: "task_type_configs",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_source_configs",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "pipeline_stage_configs",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "scoring_configs",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "sla_configs",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "task_type_configs",
                schema: "leads");
        }
    }
}
