using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Api.Infrastructure.Audit
{
    /// <inheritdoc />
    public partial class AuditImpersonator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "impersonator_user_id",
                schema: "audit",
                table: "entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_entries_tenant_id_impersonator_user_id",
                schema: "audit",
                table: "entries",
                columns: new[] { "tenant_id", "impersonator_user_id" },
                filter: "impersonator_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entries_tenant_id_impersonator_user_id",
                schema: "audit",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "impersonator_user_id",
                schema: "audit",
                table: "entries");
        }
    }
}
