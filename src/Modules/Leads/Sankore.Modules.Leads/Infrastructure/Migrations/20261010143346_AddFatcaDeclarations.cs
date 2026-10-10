using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFatcaDeclarations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fatca_declarations",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    indicia = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    documentation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    us_tin_enc = table.Column<string>(type: "text", nullable: true),
                    self_certification_doc_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    evidence_doc_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    signed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refusal_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_by_declaration_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fatca_declarations", x => x.id);
                    table.ForeignKey(
                        name: "fk_fatca_declarations_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_fatca_declarations_tenant_id_lead_id",
                schema: "leads",
                table: "fatca_declarations",
                columns: new[] { "tenant_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "ix_fatca_declarations_tenant_id_status",
                schema: "leads",
                table: "fatca_declarations",
                columns: new[] { "tenant_id", "status" },
                filter: "superseded_at IS NULL AND status IN ('UsPerson', 'Recalcitrant')");

            migrationBuilder.CreateIndex(
                name: "ux_fatca_declarations_active_per_subject",
                schema: "leads",
                table: "fatca_declarations",
                columns: new[] { "lead_id", "subject_type", "subject_id" },
                unique: true,
                filter: "superseded_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fatca_declarations",
                schema: "leads");
        }
    }
}
