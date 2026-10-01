namespace Sankore.Modules.Kyc.Tests.Features.Files;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Proves the dashboard query translates to PostgreSQL — which the rest of the suite cannot.
///
/// <para>
/// Every other test in this module runs on the EF InMemory provider, and that provider evaluates
/// almost any LINQ shape in memory. A query it accepts can still be untranslatable by Npgsql, and
/// then it fails for the first time in production. This module has already paid for that twice:
/// <c>ExecuteDeleteAsync</c> is unsupported InMemory (M12's test fails on it to this day), and on
/// .NET 10 an array's <c>Contains</c> binds to the <c>ReadOnlySpan&lt;T&gt;</c> extension and stops
/// translating at all.
/// </para>
///
/// <para>
/// No connection is opened: <c>ToQueryString</c> compiles the query and renders the SQL, which is
/// exactly the step that throws when a shape cannot be translated.
/// </para>
/// </summary>
public sealed class ListKycFilesSqlTranslationTests
{
    private static KycDbContext NpgsqlContext()
    {
        var options = new DbContextOptionsBuilder<KycDbContext>()
            // Never connected to. The provider only has to be the real one.
            .UseNpgsql("Host=localhost;Database=unused;Username=none;Password=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new KycDbContext(options, new FixedTenantContext(Guid.NewGuid()));
    }

    /// <summary>The exact shape <c>ListKycFilesHandler</c> builds, kept in step with it by hand.</summary>
    private static string RenderSql(List<Guid> perimeter, List<int> myRanks)
    {
        using var db = NpgsqlContext();

        var query = db.KycFiles
            .Where(f => f.AgencyId != null && perimeter.Contains(f.AgencyId.Value))
            .Where(f => f.Status == KycFileStatus.Validating);

        var projected = query.Select(f => new
        {
            File = f,
            NextPendingRank = db.KycApprovalSteps
                .Where(s => s.KycFileId == f.Id && s.Decision == KycApprovalDecision.Pending)
                .Min(s => (int?)s.LevelRank),
        });

        return projected
            .OrderByDescending(x => x.NextPendingRank != null && myRanks.Contains(x.NextPendingRank.Value))
            .ThenBy(x => x.File.UpdatedAt)
            .ThenBy(x => x.File.Id)
            .Skip(20)
            .Take(20)
            .ToQueryString();
    }

    [Fact]
    public void The_dashboard_query_translates_to_postgresql()
    {
        var sql = RenderSql([Guid.NewGuid()], [1, 2]);

        sql.Should().NotBeNullOrWhiteSpace();
        sql.Should().Contain("kyc_files");
    }

    [Fact]
    public void The_next_pending_rung_is_a_sql_minimum_over_the_numeric_rank()
    {
        var sql = RenderSql([Guid.NewGuid()], [2]);

        // On level_rank, never on the level column: that one is text, and MIN over it would order
        // the ladder alphabetically — agreeing with the ladder today and breaking on the first
        // rename. If this assertion ever fails because the column changed, the ordering is wrong
        // again and silently so.
        sql.Should().Contain("level_rank");
        sql.Should().MatchRegex(@"(?is)\bMIN\s*\(");
    }

    [Fact]
    public void The_perimeter_and_the_awaiting_ranks_are_both_pushed_into_sql()
    {
        var sql = RenderSql([Guid.NewGuid()], [1, 3]);

        // The perimeter must be a predicate, not a post-filter: filtering after the fact would
        // compute TotalCount and the page boundaries before it applied.
        sql.Should().Contain("agency_id");

        // ORDER BY has to carry the awaiting test, or "awaiting me first" only holds inside whatever
        // page was fetched.
        sql.Should().MatchRegex(@"(?is)ORDER\s+BY");
        sql.Should().Contain("LIMIT");
    }

    [Fact]
    public void Paging_breaks_ties_on_a_unique_column()
    {
        var sql = RenderSql([Guid.NewGuid()], [1]);

        // updated_at is not unique. Without the id as a final tie-break, two pages can repeat or
        // skip a row — the classic paging bug that only shows up with real data.
        sql.Should().Contain("updated_at");
        sql.Should().MatchRegex(@"(?is)ORDER\s+BY.*\bid\b");
    }
}
