using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <summary>
    /// Closing a missed call without calling back (TEL-F-09).
    ///
    /// <para>
    /// <b>Two columns and deliberately not three: there is no <c>dismissed_reason</c>.</b> The
    /// reason is mandatory and travels on the command, so it lands in the audit row together with
    /// its author and its instant — the same decision <c>LinkCallHandler</c> made for its
    /// correction reason, and the same argument: a column holds the LAST reason only, while what
    /// a reviewer needs is the sequence. An entry closed, reopened by a call back and closed
    /// again is a different story from one closed once, and only the audit trail can tell them
    /// apart.
    /// </para>
    ///
    /// <para>
    /// <b>No index.</b> <c>dismissed_at</c> is read as a null check inside queries already
    /// narrowed by tenant and by <c>started_at</c> — the follow-up list looks back seven days —
    /// so it filters a small set rather than selecting from a large one. An index here would be
    /// written on every journal insert to serve no query plan.
    /// </para>
    ///
    /// <para>
    /// <c>xmin</c> is absent, as it must be: it is a PostgreSQL system column and EF scaffolds it
    /// as a real one. See <c>20261008145317_InitialIntegrationSchema</c>.
    /// </para>
    /// </summary>
    public partial class AddTelephonyMissedCallDismissal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dismissed_at",
                schema: "integration",
                table: "tel_call",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "dismissed_by",
                schema: "integration",
                table: "tel_call",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "dismissed_at",
                schema: "integration",
                table: "tel_call");

            migrationBuilder.DropColumn(
                name: "dismissed_by",
                schema: "integration",
                table: "tel_call");
        }
    }
}
