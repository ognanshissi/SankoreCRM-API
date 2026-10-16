using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <summary>
    /// TEL-B-15 and TEL-B-17: the tenant's recording policy, and who has listened.
    ///
    /// <para>
    /// <b><c>xmin</c> was REMOVED BY HAND from the <c>tel_recording_policy</c> table, as it is
    /// from every migration of this module.</b> EF scaffolds the PostgreSQL system column as if it
    /// were one to create, and <c>CREATE TABLE</c> refuses it — the repo note lives on
    /// <c>20261008145317_InitialIntegrationSchema</c> and a test in the Leads project sweeps every
    /// migration for it. The optimistic concurrency token still works: the column is already
    /// there, because PostgreSQL puts it on every table.
    /// </para>
    ///
    /// <para>
    /// <b>No seeder and no default row, which departs from every other per-tenant referential of
    /// this family.</b> An absent policy means <c>Disabled</c> — the correct and safest state —
    /// so seeding one would be inventing a decision nobody took. It is also what makes "built but
    /// shipped disabled" true of the data as well as of the code.
    /// </para>
    ///
    /// <para>
    /// The access log is a table and not a column on <c>tel_recording</c> for the reason M01's
    /// <c>SensitiveDataAccessLog</c> is: a recording is listened to many times by many people, and
    /// a column would hold the last one. What a regulator asks is who has heard this.
    /// </para>
    /// </summary>
    public partial class AddTelephonyRecordingPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tel_recording_access_log",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recording_id = table.Column<Guid>(type: "uuid", nullable: false),
                    call_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    accessed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_recording_access_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tel_recording_policy",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    legal_basis = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    purpose = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    delete_at_provider = table.Column<bool>(type: "boolean", nullable: false),
                    max_duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_recording_policy", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tel_recording_access_actor",
                schema: "integration",
                table: "tel_recording_access_log",
                columns: new[] { "tenant_id", "actor_user_id", "accessed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tel_recording_access_recording",
                schema: "integration",
                table: "tel_recording_access_log",
                columns: new[] { "tenant_id", "recording_id", "accessed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tel_recording_access_log",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tel_recording_policy",
                schema: "integration");
        }
    }
}
