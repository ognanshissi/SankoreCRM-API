namespace Sankore.Modules.Leads.Features.ListConsents;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListConsentsQuery(Guid LeadId)
    : IRequest<Result<IReadOnlyList<ConsentDto>>>;
