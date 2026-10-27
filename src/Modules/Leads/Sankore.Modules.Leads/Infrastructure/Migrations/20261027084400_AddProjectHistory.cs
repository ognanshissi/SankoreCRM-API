using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_history",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    related_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reconstructed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_history_opportunities_opportunity_id",
                        column: x => x.opportunity_id,
                        principalSchema: "leads",
                        principalTable: "opportunities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_history_opportunity_id_at",
                schema: "leads",
                table: "project_history",
                columns: new[] { "opportunity_id", "at" });

            // Projects older than the history get what they already stored: their creation and,
            // when closed, their outcome with its reason. Marked reconstructed — the moment is
            // right, the author was never recorded, and the screen says so rather than inventing one.
            migrationBuilder.Sql("""
                INSERT INTO leads.project_history (id, tenant_id, opportunity_id, kind, at, actor_id, "from", "to", detail, related_id, reconstructed)
                SELECT gen_random_uuid(), o.tenant_id, o.id, 'Created', o.created_at, NULL, NULL, NULL, NULLIF(o.product, ''), NULL, TRUE
                FROM leads.opportunities o;

                INSERT INTO leads.project_history (id, tenant_id, opportunity_id, kind, at, actor_id, "from", "to", detail, related_id, reconstructed)
                SELECT gen_random_uuid(), o.tenant_id, o.id,
                       CASE o.stage WHEN 'ClosedWon' THEN 'Won' ELSE 'Lost' END,
                       COALESCE(o.closed_at, o.updated_at), NULL, NULL, NULL, o.close_reason, NULL, TRUE
                FROM leads.opportunities o
                WHERE o.stage IN ('ClosedWon', 'ClosedLost');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_history",
                schema: "leads");
        }
    }
}
