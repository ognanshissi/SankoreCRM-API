namespace Sankore.Modules.Leads.Features.GetAgentPerformance;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetAgentPerformanceQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid? AgentId
) : IRequest<Result<IReadOnlyList<AgentPerformanceDto>>>;
