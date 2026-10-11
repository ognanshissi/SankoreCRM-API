using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddComplianceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: tables here map a concurrency token onto PostgreSQL's xmin SYSTEM column.
            // EF scaffolds that as a real column, which CREATE TABLE refuses:
            //     ERROR: column name "xmin" conflicts with a system column name
            // xmin already exists on every table, so mapping it is a read-only binding and not a
            // schema change — the scaffolded lines were removed by hand. The model snapshot keeps
            // the mapping, which is what matters at run time.

            migrationBuilder.CreateTable(
                name: "compliance_settings",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fatca_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    fatca_regime = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    fatca_giin = table.Column<string>(type: "character(19)", fixedLength: true, maxLength: 19, nullable: true),
                    fatca_required_at = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fatca_for_co_holders = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    pep_post_mandate_months = table.Column<int>(type: "integer", nullable: false, defaultValue: 12),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_compliance_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_compliance_settings_tenant",
                schema: "administration",
                table: "compliance_settings",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compliance_settings",
                schema: "administration");
        }
    }
}
