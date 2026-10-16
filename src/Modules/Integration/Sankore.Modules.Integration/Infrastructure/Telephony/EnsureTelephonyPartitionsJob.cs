namespace Sankore.Modules.Integration.Infrastructure.Telephony;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// Keeps a runway of monthly partitions ahead of <c>tel_webhook_event</c> (TEL-B-03).
///
/// <para>
/// <b>Why this is part of the feature and not housekeeping around it.</b> An INSERT with no
/// matching partition does not warn and is not routed anywhere — it FAILS. The first visible
/// symptom would not be an error page either: it is the telephony journal going quiet at midnight
/// on the first of a month, while the provider keeps delivering events that are refused one by
/// one. The twin of <c>EnsureCallLogPartitionsJob</c>, deliberately not merged with it: the two
/// tables belong to different concerns and a single job would make one family's incident the
/// other's.
/// </para>
///
/// <para>
/// Neither <c>pg_cron</c> nor <c>pg_partman</c> is installed in this deployment, which is why a
/// Hangfire job carries it. Registered GLOBALLY, not per tenant: partitions are a property of the
/// table, not of a tenant.
/// </para>
/// </summary>
public sealed class EnsureTelephonyPartitionsJob(
    IntegrationDbContext db,
    TimeProvider clock,
    ILogger<EnsureTelephonyPartitionsJob> logger)
{
    /// <summary>The current month plus three, same runway as the call log's.</summary>
    internal const int MonthsAhead = 3;

    private const string Schema = "integration";
    private const string ParentTable = "tel_webhook_event";

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var statements = BuildStatements(clock.GetUtcNow());

        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql, ct);

        logger.LogInformation(
            "Telephony webhook partitions ensured up to {LastMonth} ({StatementCount} statements).",
            PartitionName(FirstOfMonth(clock.GetUtcNow()).AddMonths(MonthsAhead)), statements.Count);
    }

    /// <summary>
    /// The DDL, as text, derived from nothing but the clock.
    ///
    /// <para>
    /// Separated from the execution so the statements can be asserted on without a PostgreSQL
    /// instance: this module's tests run on the EF InMemory provider, which rejects raw SQL
    /// outright, and "the job ran" is a far weaker claim than "the job emits exactly these four
    /// idempotent statements with these UTC bounds".
    /// </para>
    ///
    /// <para>
    /// No parameters and no interpolated input: these are identifiers and range bounds inside
    /// DDL, where a placeholder is not accepted at all. Every fragment is computed from
    /// <paramref name="now"/>, so nothing from a request or a payload reaches the statement.
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
            // re-runs nightly over months it already created and must be a no-op on them, rather
            // than a failure the dashboard shows every single day.
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
    /// <c>tel_webhook_event_yYYYYmMM</c>. The <c>y</c>/<c>m</c> letters are not decoration: a bare
    /// <c>_202601</c> suffix reads like a row count in a <c>\dt</c> listing, and an operator
    /// detaching the wrong partition is an irreversible mistake.
    /// </summary>
    internal static string PartitionName(DateTime month)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{ParentTable}_y{month.Year:D4}m{month.Month:D2}");

    /// <summary>
    /// A range bound, invariant and explicitly UTC. The partition key is a <c>timestamptz</c>, so
    /// an unqualified literal would be read in the session's <c>TimeZone</c> — which makes the
    /// boundaries move with whoever ran the job, and is how a row lands one partition away from
    /// where a range query looks for it.
    /// </summary>
    private static string Bound(DateTime month)
        => month.ToString("yyyy-MM-dd HH:mm:ss'+00'", CultureInfo.InvariantCulture);
}
