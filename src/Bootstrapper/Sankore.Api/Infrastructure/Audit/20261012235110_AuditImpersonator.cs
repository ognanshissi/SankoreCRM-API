using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Api.Infrastructure.Audit
{
    /// <inheritdoc />
    public partial class AuditImpersonator : Migration
    {
        /// <summary>
        /// Hoisted out of the CreateIndex call to satisfy CA1861, which Release promotes to an
        /// error. The rule's own premise — a method called repeatedly — does not hold for a
        /// migration, but keeping the family at zero is what lets the gate mean something: the
        /// list in Directory.Build.props is there to catch a regression, and a standing
        /// violation makes every future one invisible. Nothing about what this migration DOES
        /// changes.
        /// </summary>
        private static readonly string[] ImpersonatorIndexColumns =
            ["tenant_id", "impersonator_user_id"];

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
                columns: ImpersonatorIndexColumns,
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
