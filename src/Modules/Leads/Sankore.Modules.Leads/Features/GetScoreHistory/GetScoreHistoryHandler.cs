namespace Sankore.Modules.Leads.Features.GetScoreHistory;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetScoreHistoryHandler(LeadsDbContext db)
    : IRequestHandler<GetScoreHistoryQuery, Result<IReadOnlyList<ScoreHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<ScoreHistoryDto>>> Handle(
        GetScoreHistoryQuery query, CancellationToken ct)
    {
        // Verify the lead exists and belongs to this tenant (query filter applied).
        var leadExists = await db.Leads.AnyAsync(l => l.Id == query.LeadId, ct);
        if (!leadExists)
            return Result.Fail<IReadOnlyList<ScoreHistoryDto>>("LEAD_NOT_FOUND");

        var history = await db.ScoreHistories
            .Where(s => s.LeadId == query.LeadId)
            .OrderByDescending(s => s.RecalculatedAt)
            .Select(s => new ScoreHistoryDto(
                s.Id,
                s.Score,
                s.TriggerEvent,
                s.FactorsJson,
                s.RecalculatedAt))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ScoreHistoryDto>>(history);
    }
}
