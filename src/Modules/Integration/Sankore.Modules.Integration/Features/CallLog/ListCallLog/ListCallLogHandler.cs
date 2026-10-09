namespace Sankore.Modules.Integration.Features.CallLog.ListCallLog;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ListCallLogHandler(IntegrationDbContext db, TimeProvider clock)
    : IRequestHandler<ListCallLogQuery, Result<CallLogPage>>
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 20;

    public async Task<Result<CallLogPage>> Handle(ListCallLogQuery request, CancellationToken ct)
    {
        var family = CallLogWindow.ParseFamily(request.ErrorFamily);
        if (family.IsFailure) return Result.Fail<CallLogPage>(family.Error!);

        var (from, to) = CallLogWindow.Resolve(request.From, request.To, clock.GetUtcNow());

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? DefaultPageSize : request.PageSize, 1, MaxPageSize);

        // The window first, and always present: it is what lets PostgreSQL prune the partitions,
        // and it is also the leading pair of ix_integration_call_log_tenant_at together with the
        // tenant the context's query filter adds.
        var query = db.CallLogs.Where(l => l.At >= from && l.At <= to);

        if (request.ConnectionId is { } connectionId)
            query = query.Where(l => l.ConnectionId == connectionId);

        if (request.CommandId is { } commandId)
            query = query.Where(l => l.CommandId == commandId);

        if (!string.IsNullOrWhiteSpace(request.Operation))
        {
            // Exact match, not a LIKE: Operation is a closed vocabulary the adapters declare and
            // the stats endpoint groups on, so a prefix search would be a fuzzy filter over an
            // enumerable set — and the screen can offer the list.
            var operation = request.Operation.Trim();
            query = query.Where(l => l.Operation == operation);
        }

        if (family.Value is { } errorFamily)
            query = query.Where(l => l.ErrorFamily == errorFamily);

        var totalCount = await query.CountAsync(ct);

        var rows = await query
            // Newest first: this is a diagnostic feed, and the call somebody is asking about is
            // the one that just failed.
            .OrderByDescending(l => l.At)
            // Id as the tie-break. A busy connection produces several rows inside the same
            // millisecond, and without a second key two pages can repeat or skip one of them.
            .ThenBy(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.At,
                l.ConnectionId,
                l.CommandId,
                l.Operation,
                l.Endpoint,
                l.HttpStatus,
                l.DurationMs,
                l.ErrorFamily,
                l.ErrorCode,
                l.CorrelationId,
            })
            .ToListAsync(ct);

        return Result.Ok(new CallLogPage(
            // The family is turned into text here and not in the Select above: the column is
            // mapped through HasConversion<string>, and asking the provider to translate
            // ToString() over a nullable enum is a translation that works on one provider and
            // throws on another. The page is at most MaxPageSize rows, so the mapping is free.
            Rows: [.. rows.Select(r => new CallLogRow(
                r.Id,
                r.At,
                r.ConnectionId,
                r.CommandId,
                r.Operation,
                r.Endpoint,
                r.HttpStatus,
                r.DurationMs,
                r.ErrorFamily?.ToString(),
                r.ErrorCode,
                r.CorrelationId))],
            TotalCount: totalCount,
            Page: page,
            PageSize: pageSize,
            From: from,
            To: to));
    }
}
