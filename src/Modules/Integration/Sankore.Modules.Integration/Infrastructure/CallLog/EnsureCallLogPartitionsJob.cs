namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Keeps <c>integration.integration_call_log</c> supplied with monthly partitions, three months
/// ahead (INT-01 / INT-08).
///
/// <para>
/// <b>What happens if it stops running.</b> PostgreSQL does not route a row that matches no
/// partition — it raises <c>23514 no partition of relation "integration_call_log" found for
/// row</c> and the INSERT fails. The journal swallows its own write failures, so the first
/// visible symptom is not an error page: it is the journal going quiet at midnight on the first
/// of a month, with <c>Could not journal an integration call</c> in the logs and the call-log
/// screen showing an apparently idle back-office. Every adapter call keeps working, every row is
/// lost. This is therefore <b>not optional housekeeping</b>; with three months of runway, a
/// disabled schedule has a quarter before it bites, which is enough to notice and not enough to
/// be safe.
/// </para>
///
/// <para>
/// <b>Why a Hangfire job at all.</b> Neither <c>pg_cron</c> nor <c>pg_partman</c> is installed in
/// this deployment (verified), and a declarative partitioned table has no "create on demand"
/// mode. Hangfire is the scheduler this repo already runs, its storage is the same PostgreSQL
/// instance, and the job is cheap and idempotent enough to run daily rather than monthly — a
/// monthly schedule that misses its one slot (a redeploy, a paused job) loses the month.
/// </para>
///
/// <para>
/// <b>Register it GLOBALLY, not per tenant.</b> A partition is a property of the table, not of a
/// tenant; registering it per tenant would run the same four idempotent statements once per
/// tenant and fight over the same locks for nothing.
/// </para>
///
/// <para>
/// <b>Requested cron: <c>0 2 * * *</c></b> — daily at 02:00 UTC, in the same quiet window as the
/// other nightly jobs and comfortably after midnight on the 1st, so the month that just started
/// is confirmed present rather than assumed.
/// </para>
///
/// <para>
/// The parent table and its indexes are created by the module's own migration, which this job
/// deliberately does not touch: PostgreSQL 11+ propagates an index declared on a partitioned
/// parent to every partition, existing and future, so a new partition needs no index statements
/// of its own and adding them here would create a second, divergent definition.
/// </para>
/// </summary>
public sealed class EnsureCallLogPartitionsJob(
    IntegrationDbContext db,
    TimeProvider clock,
    ILogger<EnsureCallLogPartitionsJob> logger)
{
    /// <summary>
    /// The current month plus three. Three is the runway the plan document commits to: enough
    /// that a schedule broken by a redeploy is noticed through the dashboard rather than through
    /// lost rows.
    /// </summary>
    internal const int MonthsAhead = 3;

    private const string Schema = "integration";
    private const string ParentTable = "integration_call_log";

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var statements = BuildStatements(clock.GetUtcNow());

        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql, ct);

        // Information, once a day, naming the furthest month now reachable: that single line is
        // what tells an operator reading the logs how much runway is left.
        logger.LogInformation(
            "Integration call-log partitions ensured up to {LastMonth} ({StatementCount} statements).",
            PartitionName(FirstOfMonth(clock.GetUtcNow()).AddMonths(MonthsAhead)), statements.Count);
    }

    /// <summary>
    /// The DDL, as text, derived from nothing but the clock.
    ///
    /// <para>
    /// Separated from the execution so the statements can be asserted on without a PostgreSQL
    /// instance: the test suite of this module runs on the EF InMemory provider, which rejects
    /// raw SQL outright, and "the job ran" is a far weaker claim than "the job emits exactly
    /// these four idempotent statements with these UTC bounds".
    /// </para>
    ///
    /// <para>
    /// No parameters and no interpolated input. These are identifiers and range bounds inside
    /// DDL, where a placeholder is not accepted at all; every fragment is computed from
    /// <paramref name="now"/>, so no string from a request, a payload or a configuration file
    /// reaches the statement.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> BuildStatements(DateTimeOffset now)
    {
        var firstOfThisMonth = FirstOfMonth(now);
        var statements = new List<string>(MonthsAhead + 1);

        for (var offset = 0; offset <= MonthsAhead; offset++)
        {
            var from = firstOfThisMonth.AddMonths(offset);
            var to = from.AddMonths(1);

            // CREATE TABLE IF NOT EXISTS ... PARTITION OF is the whole idempotency story: the job
            // re-runs every night over months it has already created, and must be a no-op on
            // them rather than an error the dashboard shows as a failure every single day.
            statements.Add($"""
                CREATE TABLE IF NOT EXISTS {Schema}.{PartitionName(from)}
                    PARTITION OF {Schema}.{ParentTable}
                    FOR VALUES FROM ('{Bound(from)}') TO ('{Bound(to)}');
                """);
        }

        return statements;
    }

    private static DateTime FirstOfMonth(DateTimeOffset now)
        => new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// <c>integration_call_log_yYYYYmMM</c>, as the plan document fixes it. The <c>y</c>/<c>m</c>
    /// letters are not decoration: a bare <c>_202601</c> suffix sorts and reads identically to a
    /// row count or an id in a <c>\dt</c> listing, and an operator detaching the wrong partition
    /// is an irreversible mistake.
    /// </summary>
    internal static string PartitionName(DateTime month)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{ParentTable}_y{month.Year:D4}m{month.Month:D2}");

    /// <summary>
    /// A range bound, invariant and explicitly UTC. The partition key is a
    /// <c>timestamptz</c>, so an unqualified literal would be read in the session's
    /// <c>TimeZone</c> — which makes the partition boundaries move with whoever ran the job and
    /// is how a row ends up one partition away from where a range query looks for it.
    /// </summary>
    private static string Bound(DateTime month)
        => month.ToString("yyyy-MM-dd HH:mm:ss'+00'", CultureInfo.InvariantCulture);
}
