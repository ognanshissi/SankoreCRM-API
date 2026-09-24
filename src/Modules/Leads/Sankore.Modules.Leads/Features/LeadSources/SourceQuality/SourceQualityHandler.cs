namespace Sankore.Modules.Leads.Features.LeadSources.SourceQuality;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SourceQualityHandler(LeadsDbContext db)
    : IRequestHandler<SourceQualityQuery, Result<IReadOnlyList<SourceQualityDto>>>
{
    public async Task<Result<IReadOnlyList<SourceQualityDto>>> Handle(
        SourceQualityQuery query, CancellationToken ct)
    {
        // Filter ingestions by period
        var ingestQ = db.LeadIngestions.AsQueryable();
        if (query.From.HasValue)
            ingestQ = ingestQ.Where(i => i.IngestedAt >= query.From.Value);
        if (query.To.HasValue)
            ingestQ = ingestQ.Where(i => i.IngestedAt <= query.To.Value);

        // Aggregate per source
        var ingestionStats = await ingestQ
            .GroupBy(i => i.SourceId)
            .Select(g => new
            {
                SourceId   = g.Key,
                Received   = g.Count(i => i.Status == LeadIngestionStatus.Accepted),
                Rejected   = g.Count(i => i.Status == LeadIngestionStatus.Rejected || i.Status == LeadIngestionStatus.Failed),
                Duplicates = g.Count(i => i.Status == LeadIngestionStatus.Duplicate),
            })
            .ToDictionaryAsync(x => x.SourceId, ct);

        if (ingestionStats.Count == 0)
            return Result.Ok<IReadOnlyList<SourceQualityDto>>([]);

        var sourceIds = ingestionStats.Keys.ToList();

        // Get accepted lead IDs for the period
        var acceptedLeadIds = await ingestQ
            .Where(i => sourceIds.Contains(i.SourceId)
                     && i.Status == LeadIngestionStatus.Accepted
                     && i.LeadId != Guid.Empty)
            .Select(i => new { i.SourceId, i.LeadId })
            .ToListAsync(ct);

        var leadIdsBySource = acceptedLeadIds
            .GroupBy(x => x.SourceId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.LeadId).ToHashSet());

        // Get lead statuses for conversion/contact counts
        var allLeadIds = leadIdsBySource.Values.SelectMany(x => x).Distinct().ToList();

        var leadStatuses = await db.Leads
            .Where(l => allLeadIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Status })
            .ToDictionaryAsync(l => l.Id, l => l.Status, ct);

        // Check contacted (has assignment with first contact)
        var contactedLeadIds = await db.LeadAssignments
            .Where(a => allLeadIds.Contains(a.LeadId) && a.FirstContactAt != null)
            .Select(a => a.LeadId)
            .Distinct()
            .ToHashSetAsync(ct);

        // Load sources for metadata + cost
        var sources = await db.LeadSourceConfigs
            .Where(s => sourceIds.Contains(s.Id))
            .ToListAsync(ct);

        var result = sources.Select(source =>
        {
            ingestionStats.TryGetValue(source.Id, out var stats);
            leadIdsBySource.TryGetValue(source.Id, out var leads);

            var received   = stats?.Received ?? 0;
            var rejected   = stats?.Rejected ?? 0;
            var duplicates = stats?.Duplicates ?? 0;

            var contacted = leads?.Count(id => contactedLeadIds.Contains(id)) ?? 0;
            var converted = leads?.Count(id =>
                leadStatuses.TryGetValue(id, out var s) && s == LeadStatus.Converted) ?? 0;

            var costAmount   = source.CostPerLead?.Amount ?? 0;
            var costCurrency = source.CostPerLead?.Currency;
            var totalCost    = costAmount * received;
            var costPerConverted = converted > 0 ? totalCost / converted : (decimal?)null;

            return new SourceQualityDto(
                source.Id, source.Code, source.Label,
                received, rejected, duplicates, contacted, converted,
                totalCost, costCurrency, costPerConverted);
        }).ToList();

        return Result.Ok<IReadOnlyList<SourceQualityDto>>(result);
    }
}
