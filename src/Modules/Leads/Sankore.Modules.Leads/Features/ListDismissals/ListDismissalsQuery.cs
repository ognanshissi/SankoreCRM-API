namespace Sankore.Modules.Leads.Features.ListDismissals;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListDismissalsQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<DismissalDto>>>;
