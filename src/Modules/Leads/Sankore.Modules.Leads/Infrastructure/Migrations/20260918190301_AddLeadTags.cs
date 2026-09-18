using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lead_tags",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    added_by = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lead_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_lead_tags_leads_lead_id",
                        column: x => x.lead_id,
                        principalSchema: "leads",
                        principalTable: "leads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lead_tags_lead_id_tag",
                schema: "leads",
                table: "lead_tags",
                columns: new[] { "lead_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lead_tags_tenant_id_tag",
                schema: "leads",
                table: "lead_tags",
                columns: new[] { "tenant_id", "tag" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_tags",
                schema: "leads");
        }
    }
}
