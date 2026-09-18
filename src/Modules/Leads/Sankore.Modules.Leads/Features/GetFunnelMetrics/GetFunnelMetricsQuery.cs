namespace Sankore.Modules.Leads.Features.GetFunnelMetrics;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetFunnelMetricsQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? AgencyId
) : IRequest<Result<FunnelMetricsDto>>;
