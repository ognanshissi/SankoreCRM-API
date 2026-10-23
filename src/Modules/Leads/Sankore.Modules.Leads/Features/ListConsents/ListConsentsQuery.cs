namespace Sankore.Modules.Leads.Features.ListConsents;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed record ListConsentsQuery(Guid LeadId)
    : ILeadReadRequest, IRequest<Result<IReadOnlyList<ConsentDto>>>;
