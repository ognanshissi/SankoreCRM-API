namespace Sankore.Modules.Integration.Features.Insurance.Statements;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// One month's bordereau, header only.
///
/// <para>
/// The counts and the totals are the statement's own COLUMNS, not a sum over the lines read back:
/// a finalised document must render the same figures in a year, whatever happened to the policies
/// since.
/// </para>
/// </summary>
public sealed record StatementDto(
    Guid Id,
    Guid ConnectionId,
    string Period,
    string Status,
    int SubscriptionCount,
    int CancellationCount,
    int ReversalCount,
    decimal PremiumCollected,
    decimal PremiumReversed,
    decimal CommissionAmount,
    string Currency,
    DateTimeOffset GeneratedAt,
    DateTimeOffset? FinalisedAt,
    Guid? FinalisedBy,
    DateTimeOffset? TransmittedAt,
    string? TransmitFailureDetail,
    bool IsRebuildable)
{
    internal static StatementDto From(InsurerStatement s) => new(
        Id: s.Id,
        ConnectionId: s.ConnectionId,
        Period: s.Period,
        Status: s.Status.ToString(),
        SubscriptionCount: s.SubscriptionCount,
        CancellationCount: s.CancellationCount,
        ReversalCount: s.ReversalCount,
        PremiumCollected: s.PremiumCollected,
        PremiumReversed: s.PremiumReversed,
        CommissionAmount: s.CommissionAmount,
        Currency: s.Currency,
        GeneratedAt: s.GeneratedAt,
        FinalisedAt: s.FinalisedAt,
        FinalisedBy: s.FinalisedBy,
        TransmittedAt: s.TransmittedAt,
        TransmitFailureDetail: s.TransmitFailureDetail,
        // The aggregate's own definition, so a screen does not re-derive "can I still rebuild
        // this" from a status name and get it wrong on a TransmitFailed month.
        IsRebuildable: s.IsRebuildable);
}

/// <summary>
/// One printed line.
///
/// <para>
/// <c>commissionRate</c> is exposed alongside the amount because it is the rate APPLIED, copied at
/// write time — an insurer reconciling a bordereau checks the arithmetic line by line, and a rate
/// the reader has to look up in today's catalogue is the rate that was NOT used.
/// </para>
/// </summary>
public sealed record StatementLineDto(
    Guid Id,
    string LineType,
    Guid PolicyId,
    Guid? InstalmentId,
    Guid? SubscriptionId,
    Guid CrmCustomerId,
    DateOnly OccurredOn,
    decimal Amount,
    string Currency,
    decimal CommissionRate,
    decimal CommissionAmount,
    string PolicyNumber,
    string InsurerProductCode,
    string? CbsReference)
{
    internal static StatementLineDto From(InsurerStatementLine l) => new(
        Id: l.Id,
        LineType: l.LineType.ToString(),
        PolicyId: l.PolicyId,
        InstalmentId: l.InstalmentId,
        SubscriptionId: l.SubscriptionId,
        CrmCustomerId: l.CrmCustomerId,
        OccurredOn: l.OccurredOn,
        Amount: l.Amount,
        Currency: l.Currency,
        CommissionRate: l.CommissionRate,
        CommissionAmount: l.CommissionAmount,
        PolicyNumber: l.PolicyNumber,
        InsurerProductCode: l.InsurerProductCode,
        CbsReference: l.CbsReference);
}

/// <summary>The tenant's bordereaux, newest period first. Optionally narrowed to one insurer.</summary>
internal sealed record ListStatementsQuery(Guid? ConnectionId, string? Period)
    : IRequest<Result<IReadOnlyList<StatementDto>>>;

internal sealed class ListStatementsHandler(IntegrationDbContext db)
    : IRequestHandler<ListStatementsQuery, Result<IReadOnlyList<StatementDto>>>
{
    /// <summary>
    /// Two years of monthly documents across a handful of insurers. A ceiling rather than a
    /// pager, like the claims list, and for the same reason: the set is tens of rows.
    /// </summary>
    private const int MaxRows = 240;

    public async Task<Result<IReadOnlyList<StatementDto>>> Handle(
        ListStatementsQuery query, CancellationToken ct)
    {
        if (query.Period is not null && !StatementPeriod.IsValid(query.Period))
            return Result.Fail<IReadOnlyList<StatementDto>>(StatementErrors.PeriodInvalid);

        var rows = db.InsurerStatements.AsQueryable();

        if (query.ConnectionId is { } connectionId)
            rows = rows.Where(s => s.ConnectionId == connectionId);

        if (query.Period is { Length: 7 } period)
            rows = rows.Where(s => s.Period == period);

        var statements = await rows
            // Period is YYYY-MM, so its string order IS its chronological order — which is the
            // reason that shape was chosen over two integer columns.
            .OrderByDescending(s => s.Period)
            .ThenBy(s => s.ConnectionId)
            .Take(MaxRows)
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<StatementDto>>([.. statements.Select(StatementDto.From)]);
    }
}

/// <summary>One bordereau with its printed detail.</summary>
internal sealed record GetStatementQuery(Guid StatementId)
    : IRequest<Result<StatementDetailDto>>;

public sealed record StatementDetailDto(
    StatementDto Statement,
    IReadOnlyList<StatementLineDto> Lines);

internal sealed class GetStatementHandler(IntegrationDbContext db)
    : IRequestHandler<GetStatementQuery, Result<StatementDetailDto>>
{
    public async Task<Result<StatementDetailDto>> Handle(
        GetStatementQuery query, CancellationToken ct)
    {
        var statement = await db.InsurerStatements
            .FirstOrDefaultAsync(s => s.Id == query.StatementId, ct);

        if (statement is null)
            return Result.Fail<StatementDetailDto>(StatementErrors.NotFound);

        var lines = await db.InsurerStatementLines
            .Where(l => l.StatementId == statement.Id)
            // The order a printed bordereau is read in, and the order the generation wrote them
            // in: by day, then by contract number.
            .OrderBy(l => l.OccurredOn)
            .ThenBy(l => l.PolicyNumber)
            .ToListAsync(ct);

        return Result.Ok(new StatementDetailDto(
            StatementDto.From(statement),
            [.. lines.Select(StatementLineDto.From)]));
    }
}
