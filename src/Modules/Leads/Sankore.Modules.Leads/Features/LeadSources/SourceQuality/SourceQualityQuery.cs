namespace Sankore.Modules.Leads.Features.LeadSources.SourceQuality;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record SourceQualityQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null
) : IRequest<Result<IReadOnlyList<SourceQualityDto>>>;

public sealed record SourceQualityDto(
    Guid SourceId,
    string Code,
    string Label,
    int Received,
    int Rejected,
    int Duplicates,
    int Contacted,
    int Converted,
    decimal TotalCost,
    string? CostCurrency,
    decimal? CostPerConvertedLead);
