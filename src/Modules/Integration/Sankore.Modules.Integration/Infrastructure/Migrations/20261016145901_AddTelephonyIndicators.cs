using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <summary>
    /// The indicators' read model, and the column that makes a per-agency one possible
    /// (TEL-B-19).
    ///
    /// <para>
    /// <b><c>tel_call.agency_id</c> is nullable and is NOT backfilled.</b> Deliberately: it
    /// records the agency a call belonged to AT THE TIME, and the only source for a call already
    /// journalled is where its agent sits today — which is a different fact. Filling it from
    /// there would silently move last quarter's conversations into whatever branch an agent has
    /// since been transferred to, and the per-agency figures would then be wrong in a way nobody
    /// could detect. Existing calls stay in the "no agency" bucket, visible to whoever sees the
    /// tenant whole, and every call from this migration onward carries its own.
    /// </para>
    ///
    /// <para>
    /// <b>No <c>xmin</c> on <c>tel_call_daily_stat</c>, and no row version of any kind.</b> The
    /// table is derived: two writers disagreeing about a bucket is not a conflict to detect, it
    /// is two recomputations of the same day producing the same figures. The repo-wide rule still
    /// applies to everything else — <c>xmin</c> is a system column and must never appear in a
    /// migration's <c>Up()</c>, see <c>20261008145317_InitialIntegrationSchema</c>.
    /// </para>
    ///
    /// <para>
    /// Both grouping columns are non-nullable with <c>Guid.Empty</c> for "none", which is what
    /// makes <c>ux_tel_call_daily_stat_bucket</c> constrain every row: PostgreSQL treats each
    /// null as distinct, so a nullable agency there would let one run stack a duplicate bucket on
    /// the last one's.
    /// </para>
    /// </summary>
    public partial class AddTelephonyIndicators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "agency_id",
                schema: "integration",
                table: "tel_call",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tel_call_daily_stat",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stat_date = table.Column<DateOnly>(type: "date", nullable: false),
                    crm_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agency_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_count = table.Column<int>(type: "integer", nullable: false),
                    outbound_count = table.Column<int>(type: "integer", nullable: false),
                    answered_count = table.Column<int>(type: "integer", nullable: false),
                    missed_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    contact_count = table.Column<int>(type: "integer", nullable: false),
                    wrapped_up_count = table.Column<int>(type: "integer", nullable: false),
                    talk_seconds = table.Column<int>(type: "integer", nullable: false),
                    ring_seconds = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tel_call_daily_stat", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tel_call_started",
                schema: "integration",
                table: "tel_call",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tel_call_daily_stat_agency",
                schema: "integration",
                table: "tel_call_daily_stat",
                columns: new[] { "tenant_id", "agency_id", "stat_date" });

            migrationBuilder.CreateIndex(
                name: "ux_tel_call_daily_stat_bucket",
                schema: "integration",
                table: "tel_call_daily_stat",
                columns: new[] { "tenant_id", "stat_date", "crm_user_id", "agency_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tel_call_daily_stat",
                schema: "integration");

            migrationBuilder.DropIndex(
                name: "ix_tel_call_started",
                schema: "integration",
                table: "tel_call");

            migrationBuilder.DropColumn(
                name: "agency_id",
                schema: "integration",
                table: "tel_call");
        }
    }
}
