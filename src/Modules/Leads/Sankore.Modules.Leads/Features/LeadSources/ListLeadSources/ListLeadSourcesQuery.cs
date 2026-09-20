namespace Sankore.Modules.Leads.Features.LeadSources.ListLeadSources;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListLeadSourcesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<LeadSourceDto>>>;
