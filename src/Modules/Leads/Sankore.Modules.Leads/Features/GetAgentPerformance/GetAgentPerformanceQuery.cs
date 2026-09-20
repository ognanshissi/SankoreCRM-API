namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetAgentPerformanceQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? AgentId,
    Guid? AgencyId = null,
    Guid? CurrentUserId = null,
    IReadOnlyList<string>? CurrentUserRoles = null
) : IRequest<Result<IReadOnlyList<AgentPerformanceDto>>>;
