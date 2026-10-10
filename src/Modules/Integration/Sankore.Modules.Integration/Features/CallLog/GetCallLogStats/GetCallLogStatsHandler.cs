namespace Sankore.Modules.Integration.Features.CallLog.GetCallLogStats;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

using DomainErrorFamily = Sankore.Modules.Integration.PublicApi.ErrorFamily;

internal sealed class GetCallLogStatsHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<GetCallLogStatsQuery, Result<CallLogStats>>
{
    /// <summary>
    /// How many operations the answer may describe. A guard, not a feature: the grouping key is a
    /// closed vocabulary of a few dozen adapter operations, so reaching this number means an
    /// adapter is putting something variable in <c>Operation</c> — and the per-operation p95
    /// below costs one query each.
    /// </summary>
    private const int MaxOperations = 100;

    public async Task<Result<CallLogStats>> Handle(GetCallLogStatsQuery request, CancellationToken ct)
    {
        var family = CallLogWindow.ParseFamily(request.ErrorFamily);
        if (family.IsFailure) return Result.Fail<CallLogStats>(family.Error!);

        var (from, to) = CallLogWindow.Resolve(request.From, request.To, clock.GetUtcNow());

        var scoped = Scope(request, family.Value, from, to);

        // One grouped pass for everything that is a counter or an extremum. Expressed in SQL and
        // not over materialised rows: a busy tenant's week is millions of calls, and the answer is
        // a few dozen rows whatever the volume.
        var grouped = await scoped
            .GroupBy(l => l.Operation)
            .Select(g => new
            {
                Operation = g.Key,
                Calls = g.Count(),
                Failures = g.Count(l => l.ErrorFamily != null),
                Transient = g.Count(l => l.ErrorFamily == DomainErrorFamily.Transient),
                Functional = g.Count(l => l.ErrorFamily == DomainErrorFamily.Functional),
                Technical = g.Count(l => l.ErrorFamily == DomainErrorFamily.Technical),
                AvgDurationMs = g.Average(l => (double)l.DurationMs),
                MaxDurationMs = g.Max(l => l.DurationMs),
            })
            .OrderByDescending(x => x.Calls)
            .ThenBy(x => x.Operation)
            .Take(MaxOperations)
            .ToListAsync(ct);

        var operations = new List<CallLogOperationStats>(grouped.Count);

        foreach (var row in grouped)
        {
            operations.Add(new CallLogOperationStats(
                Operation: row.Operation,
                Calls: row.Calls,
                Failures: row.Failures,
                TransientFailures: row.Transient,
                FunctionalFailures: row.Functional,
                TechnicalFailures: row.Technical,
                // Rounded to the millisecond the column is measured in. A duration reported to
                // four decimals would claim a precision a Stopwatch around an HTTP call does not
                // have.
                AvgDurationMs: (long)Math.Round(row.AvgDurationMs, MidpointRounding.AwayFromZero),
                P95DurationMs: await P95Async(request, family.Value, from, to, row.Operation, row.Calls, ct),
                MaxDurationMs: row.MaxDurationMs));
        }

        return Result.Ok(new CallLogStats(
            From: from,
            To: to,
            TotalCalls: operations.Sum(o => o.Calls),
            FailedCalls: operations.Sum(o => o.Failures),
            Operations: operations));
    }

    /// <summary>
    /// The nearest-rank p95 for one operation: skip to position <c>ceil(0.95 × n) - 1</c> of the
    /// ascending durations and read that row.
    ///
    /// <para>
    /// One query per operation, deliberately, instead of PostgreSQL's
    /// <c>percentile_cont(0.95) WITHIN GROUP</c>. That aggregate has no LINQ translation, so it
    /// would mean <c>FromSqlRaw</c> — which pins this handler to Npgsql and makes the whole read
    /// side untestable on the EF InMemory provider the module's tests run on. The cost is bounded
    /// by the number of distinct operations (a closed vocabulary, capped above), each query is a
    /// <c>LIMIT 1</c> over the same pruned partitions and index as the grouped pass, and the
    /// answer is an exact observed duration rather than an interpolated one.
    /// </para>
    /// </summary>
    private async Task<long> P95Async(
        GetCallLogStatsQuery request,
        DomainErrorFamily? family,
        DateTimeOffset from,
        DateTimeOffset to,
        string operation,
        int calls,
        CancellationToken ct)
    {
        // n == 1 has a single candidate and n == 0 cannot occur: the operation came from a group
        // that counted at least one row. Short-circuited to keep a trivial case off the database.
        if (calls <= 1)
        {
            return await Scope(request, family, from, to)
                .Where(l => l.Operation == operation)
                .MaxAsync(l => (long?)l.DurationMs, ct) ?? 0;
        }

        var rank = (int)Math.Ceiling(0.95 * calls);
        var skip = Math.Clamp(rank - 1, 0, calls - 1);

        return await Scope(request, family, from, to)
            .Where(l => l.Operation == operation)
            .OrderBy(l => l.DurationMs)
            // Id as the tie-break so the skip is over a deterministic order; without it two
            // equal durations can swap between the count and this read.
            .ThenBy(l => l.Id)
            .Skip(skip)
            .Select(l => l.DurationMs)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The filtered set, rebuilt per query rather than shared as an <c>IQueryable</c> field: the
    /// window predicate must be the first thing on every one of them, because it is what prunes
    /// the partitions, and a helper makes it impossible for one of the per-operation reads to be
    /// written without it.
    ///
    /// <para>
    /// Tenant isolation is the context's own query filter, so a connection id from another tenant
    /// narrows to nothing and the answer is an empty set of operations — never a refusal that
    /// would confirm the connection exists.
    /// </para>
    /// </summary>
    private IQueryable<IntegrationCallLog> Scope(
        GetCallLogStatsQuery request, DomainErrorFamily? family, DateTimeOffset from, DateTimeOffset to)
    {
        var query = db.CallLogs.Where(l => l.At >= from && l.At <= to);

        if (request.ConnectionId is { } connectionId)
            query = query.Where(l => l.ConnectionId == connectionId);

        if (family is { } errorFamily)
            query = query.Where(l => l.ErrorFamily == errorFamily);

        return query;
    }
}
