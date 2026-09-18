namespace Sankore.Modules.Leads.Features.GetSlaBreaches;

using MediatR;
using Sankore.Shared.Kernel;

public sealed record GetSlaBreachesQuery(
    Guid? AgentId,
    Guid? AgencyId
) : IRequest<Result<IReadOnlyList<SlaBreachDto>>>;
