namespace Sankore.Modules.Leads.Features.ListDismissals;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed record ListDismissalsQuery(Guid LeadId)
    : ILeadReadRequest, IRequest<Result<IReadOnlyList<DismissalDto>>>;
