namespace Sankore.Modules.Leads.Features.GetPipeline;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class GetPipelineHandler(LeadsDbContext db)
    : IRequestHandler<GetPipelineQuery, Result<PipelineView>>
{
    // Stages shown on the board (exclude terminal stages).
    private static readonly PipelineStage[] BoardStages =
    [
        PipelineStage.New,
        PipelineStage.ContactAttempted,
        PipelineStage.ContactEstablished,
        PipelineStage.NeedIdentified,
        PipelineStage.Qualified,
        PipelineStage.ProductProposed,
        PipelineStage.ApplicationStarted,
        PipelineStage.DocumentCollection,
        PipelineStage.ApplicationCompleted,
        PipelineStage.ApprovalPending,
    ];

    public async Task<Result<PipelineView>> Handle(
        GetPipelineQuery query, CancellationToken ct)
    {
        var q = db.Leads.AsNoTracking()
            .Where(l => l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Disqualified
                     && l.Status != LeadStatus.Archived);

        if (query.AgencyId.HasValue)
            q = q.Where(l => l.AgencyId == query.AgencyId.Value);

        if (query.OwnerId.HasValue)
            q = q.Where(l => l.OwnerId == query.OwnerId.Value);

        // Single query: group by stage, count + take top N cards per stage.
        var stageGroups = await q
            .GroupBy(l => l.PipelineStage)
            .Select(g => new
            {
                Stage = g.Key,
                TotalCount = g.Count(),
            })
            .ToListAsync(ct);

        var maxPerStage = Math.Clamp(query.MaxPerStage, 1, 100);

        var columns = new List<PipelineColumn>(BoardStages.Length);

        foreach (var stage in BoardStages)
        {
            var group = stageGroups.FirstOrDefault(g => g.Stage == stage);
            var total = group?.TotalCount ?? 0;

            IReadOnlyList<PipelineCard> cards;

            if (total > 0)
            {
                cards = await q
                    .Where(l => l.PipelineStage == stage)
                    .OrderByDescending(l => l.Score)
                    .ThenByDescending(l => l.CreatedAt)
                    .Take(maxPerStage)
                    .Select(l => new PipelineCard(
                        l.Id,
                        l.FullName,
                        l.Email,
                        l.PhoneNumber,
                        l.InterestedProduct,
                        l.Score,
                        l.IntentLevel.ToString(),
                        l.OwnerId,
                        l.CreatedAt,
                        l.LastActivityAt,
                        l.UpdatedAt))
                    .ToListAsync(ct);
            }
            else
            {
                cards = [];
            }

            columns.Add(new PipelineColumn(stage.ToString(), total, cards));
        }

        return Result.Ok(new PipelineView(columns));
    }
}
