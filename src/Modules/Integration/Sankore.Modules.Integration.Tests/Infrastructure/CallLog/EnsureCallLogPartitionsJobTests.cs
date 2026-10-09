namespace Sankore.Modules.Integration.Tests.Infrastructure.CallLog;

using System.Globalization;
using FluentAssertions;
using Sankore.Modules.Integration.Infrastructure.CallLog;
using Xunit;

/// <summary>
/// The monthly partitions of <c>integration_call_log</c>.
///
/// <para>
/// Asserted on the SQL text rather than against a database: the module's suite runs on the EF
/// InMemory provider, which rejects raw SQL, and a container-backed test would prove the
/// statements execute without proving they cover the right months. What actually goes wrong here
/// is a date, not a syntax — a job that creates the next three months but not the current one
/// fails on the day it is deployed, and a job whose bounds are read in the server's local time
/// zone puts rows one partition away from where a range query looks for them.
/// </para>
///
/// <para>
/// The failure mode these tests guard against is silent. The journal swallows its own write
/// failures, so a missing partition does not raise anything a user sees: the call-log screen just
/// shows an idle back-office from the first of the month onwards.
/// </para>
/// </summary>
public sealed class EnsureCallLogPartitionsJobTests
{
    [Fact]
    public void Creates_the_current_month_and_the_next_three()
    {
        var statements = EnsureCallLogPartitionsJob.BuildStatements(
            DateTimeOffset.Parse("2026-10-08T14:32:11Z", CultureInfo.InvariantCulture));

        statements.Should().HaveCount(4, "the current month plus three is the runway the plan commits to");

        statements[0].Should().Contain("integration_call_log_y2026m10");
        statements[1].Should().Contain("integration_call_log_y2026m11");
        statements[2].Should().Contain("integration_call_log_y2026m12");
        statements[3].Should().Contain(
            "integration_call_log_y2027m01", "the window has to roll over the year");
    }

    [Fact]
    public void The_current_month_is_included_even_when_the_job_runs_on_the_last_day_of_it()
    {
        // The deployment-day case: a job that only looked ahead would leave today unpartitioned
        // and every call from this instant on unjournalled.
        var statements = EnsureCallLogPartitionsJob.BuildStatements(
            DateTimeOffset.Parse("2026-01-31T23:59:59Z", CultureInfo.InvariantCulture));

        statements[0].Should().Contain("integration_call_log_y2026m01");
        statements[^1].Should().Contain("integration_call_log_y2026m04");
    }

    [Fact]
    public void Every_statement_is_idempotent_and_schema_qualified()
    {
        var statements = EnsureCallLogPartitionsJob.BuildStatements(
            DateTimeOffset.Parse("2026-10-08T02:00:00Z", CultureInfo.InvariantCulture));

        foreach (var sql in statements)
        {
            sql.Should().Contain(
                "CREATE TABLE IF NOT EXISTS",
                "the job re-runs nightly over months it already created and must be a no-op on "
                + "them, not a dashboard failure every single day");

            sql.Should().Contain("PARTITION OF integration.integration_call_log");

            sql.Should().Contain(
                "integration.integration_call_log_y",
                "unqualified DDL depends on the session's search_path, which a migration bundle "
                + "and a Hangfire worker do not share");
        }
    }

    [Fact]
    public void The_bounds_are_the_month_boundaries_in_UTC()
    {
        var statements = EnsureCallLogPartitionsJob.BuildStatements(
            DateTimeOffset.Parse("2026-10-08T14:00:00Z", CultureInfo.InvariantCulture));

        statements[0].Should().Contain(
            "FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00')",
            "the partition key is a timestamptz: an unqualified literal is read in the session's "
            + "TimeZone and the boundaries would move with whoever ran the job");

        // Half-open, as PostgreSQL range partitions are: the upper bound belongs to the NEXT
        // partition. The 1st of November appears as the end of October's range and as the start
        // of November's, and never in both as an inclusive value.
        statements[1].Should().Contain("FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00')");
    }

    [Fact]
    public void A_february_in_a_leap_year_is_still_one_partition()
    {
        // Named months, never day arithmetic: FOR VALUES uses the first of the next month as its
        // exclusive upper bound, so February's length never enters into it.
        var statements = EnsureCallLogPartitionsJob.BuildStatements(
            DateTimeOffset.Parse("2028-02-29T12:00:00Z", CultureInfo.InvariantCulture));

        statements[0].Should().Contain(
            "FOR VALUES FROM ('2028-02-01 00:00:00+00') TO ('2028-03-01 00:00:00+00')");
    }

    [Fact]
    public void The_partition_name_is_the_one_the_plan_document_fixes()
        => EnsureCallLogPartitionsJob.PartitionName(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be(
                "integration_call_log_y2026m03",
                "zero-padded, with the y/m letters: an operator reading \\dt before detaching a "
                + "partition is doing something irreversible");
}
