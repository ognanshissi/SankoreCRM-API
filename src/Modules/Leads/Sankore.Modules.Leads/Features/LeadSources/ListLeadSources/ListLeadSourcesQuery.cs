namespace Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record ListLeadSourcesQuery(
    LeadChannelType? ChannelType = null,
    IntegrationMode? Mode = null,
    LeadSourceStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20
) : IRequest<Result<PagedResult<LeadSourceListDto>>>;
