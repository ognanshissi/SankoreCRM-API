namespace Sankore.Modules.Leads.Features.GetLeadStats;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record GetLeadStatsQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null
) : IRequest<Result<LeadStatsDto>>;

public sealed record LeadStatsDto(
    int Total,
    IReadOnlyList<StatusCount> ByStatus,
    IReadOnlyList<SourceCount> BySource,
    IReadOnlyList<StageCount> ByPipelineStage,
    int ConvertedCount,
    double ConversionRate);

public sealed record StatusCount(LeadStatus Status, int Count);
public sealed record SourceCount(LeadSource Source, int Count);
public sealed record StageCount(PipelineStage Stage, int Count);
