using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <summary>
    /// ASS-12: the trace of a purge, and the index its worklist reads.
    ///
    /// <para>
    /// <c>purged_at</c> is nullable and NO backfill stamps it: null means the row still holds its
    /// evidence, which is true of every row that exists when this migration runs. Stamping one
    /// would assert that a destruction happened.
    /// </para>
    ///
    /// <para>
    /// <c>retain_until</c> stays null on those rows too, and the purge job's first pass is what
    /// dates them — from each row's own capture date, not from the night it runs. Doing it in SQL
    /// here would apply today's configured period as a schema change, which is a retention
    /// decision taken by a migration.
    /// </para>
    /// </summary>
    public partial class AddInsuranceEvidencePurgeTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "purged_at",
                schema: "integration",
                table: "ins_consent_proof",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ins_consent_proof_retention",
                schema: "integration",
                table: "ins_consent_proof",
                columns: new[] { "tenant_id", "retain_until", "purged_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ins_consent_proof_retention",
                schema: "integration",
                table: "ins_consent_proof");

            migrationBuilder.DropColumn(
                name: "purged_at",
                schema: "integration",
                table: "ins_consent_proof");
        }
    }
}
