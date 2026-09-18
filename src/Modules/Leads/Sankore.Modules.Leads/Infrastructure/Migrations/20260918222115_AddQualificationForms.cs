using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualificationForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // score_histories was present in the EF model snapshot but never captured
            // in a prior migration — create it here before adding the new column.
            migrationBuilder.CreateTable(
                name: "score_histories",
                schema: "leads",
                columns: table => new
                {
                    id               = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id        = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id          = table.Column<Guid>(type: "uuid", nullable: false),
                    score            = table.Column<int>(type: "integer", nullable: false),
                    factors_json     = table.Column<string>(type: "jsonb", nullable: false),
                    trigger_event    = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    recalculated_at  = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_score_histories", x => x.id);
                    table.ForeignKey(
                        name: "fk_score_histories_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_score_histories_lead_id_recalculated_at",
                schema: "leads",
                table: "score_histories",
                columns: new[] { "lead_id", "recalculated_at" });

            migrationBuilder.AddColumn<Guid>(
                name: "qualification_response_id",
                schema: "leads",
                table: "score_histories",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "qualification_responses",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    answered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    computed_score = table.Column<int>(type: "integer", nullable: false),
                    answers_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qualification_responses", x => x.id);
                    table.ForeignKey(
                        name: "fk_qualification_responses_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "qualification_templates",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    product_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qualification_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "qualification_questions",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    options_json = table.Column<string>(type: "jsonb", nullable: true),
                    weight = table.Column<int>(type: "integer", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_qualification_questions", x => x.id);
                    table.ForeignKey(
                        name: "fk_qualification_questions_qualification_templates_template_id",
                        column: x => x.template_id,
                        principalSchema: "leads",
                        principalTable: "qualification_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_score_histories_qualification_response_id",
                schema: "leads",
                table: "score_histories",
                column: "qualification_response_id",
                filter: "qualification_response_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_qualification_questions_template_id_order",
                schema: "leads",
                table: "qualification_questions",
                columns: new[] { "template_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_responses_lead_id",
                schema: "leads",
                table: "qualification_responses",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_qualification_responses_tenant_id_lead_id",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_responses_tenant_id_template_id",
                schema: "leads",
                table: "qualification_responses",
                columns: new[] { "tenant_id", "template_id" });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_is_active",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_qualification_templates_tenant_id_product_name",
                schema: "leads",
                table: "qualification_templates",
                columns: new[] { "tenant_id", "product_name" },
                filter: "product_name IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qualification_questions",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "qualification_responses",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "qualification_templates",
                schema: "leads");

            migrationBuilder.DropTable(
                name: "score_histories",
                schema: "leads");
        }
    }
}
