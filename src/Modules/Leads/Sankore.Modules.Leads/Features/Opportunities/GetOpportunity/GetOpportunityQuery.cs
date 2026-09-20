namespace Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetOpportunityQuery(Guid OpportunityId)
    : IRequest<Result<OpportunityDto>>;
