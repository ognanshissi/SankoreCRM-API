using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Leads.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectQuoteIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "issued_version",
                schema: "leads",
                table: "project_quotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_issued_at",
                schema: "leads",
                table: "project_quotes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "number",
                schema: "leads",
                table: "project_quotes",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_quote_issues",
                schema: "leads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opportunity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_excl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    total_incl_tax = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_quote_issues", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_quote_issues_opportunities_opportunity_id",
                        column: x => x.opportunity_id,
                        principalSchema: "leads",
                        principalTable: "opportunities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_quote_issues_opportunity_id_version",
                schema: "leads",
                table: "project_quote_issues",
                columns: new[] { "opportunity_id", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_quote_issues",
                schema: "leads");

            migrationBuilder.DropColumn(
                name: "issued_version",
                schema: "leads",
                table: "project_quotes");

            migrationBuilder.DropColumn(
                name: "last_issued_at",
                schema: "leads",
                table: "project_quotes");

            migrationBuilder.DropColumn(
                name: "number",
                schema: "leads",
                table: "project_quotes");
        }
    }
}
