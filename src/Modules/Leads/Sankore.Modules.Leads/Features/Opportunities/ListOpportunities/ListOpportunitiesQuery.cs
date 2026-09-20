namespace Sankore.Modules.Leads.Features.Opportunities.ListOpportunities;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListOpportunitiesQuery(
    Guid? LeadId = null,
    Guid? CustomerEntityId = null,
    OpportunityStage? Stage = null
) : IRequest<Result<IReadOnlyList<OpportunityDto>>>;
