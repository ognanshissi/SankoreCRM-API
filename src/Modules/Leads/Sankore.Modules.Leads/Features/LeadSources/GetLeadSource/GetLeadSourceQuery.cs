namespace Sankore.Modules.Leads.Features.LeadSources.GetLeadSource;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetLeadSourceQuery(Guid SourceId)
    : IRequest<Result<LeadSourceDetailDto>>;
