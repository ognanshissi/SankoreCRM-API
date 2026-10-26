using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InsuranceLot2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "insurance_distributor_documents",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issuer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    file_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_insurance_distributor_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "insurance_distributor_profiles",
                schema: "administration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    registration_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    legal_mention = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    annual_smig = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_insurance_distributor_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_insurance_distributor_documents_tenant_id_kind_superseded_at",
                schema: "administration",
                table: "insurance_distributor_documents",
                columns: new[] { "tenant_id", "kind", "superseded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_insurance_distributor_profiles_tenant_id",
                schema: "administration",
                table: "insurance_distributor_profiles",
                column: "tenant_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "insurance_distributor_documents",
                schema: "administration");

            migrationBuilder.DropTable(
                name: "insurance_distributor_profiles",
                schema: "administration");
        }
    }
}
