namespace Sankore.Modules.Leads.Features.SlaConfigs.ListSlaConfigs;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListSlaConfigsQuery(bool? ActiveOnly = null, Guid? AgencyId = null)
    : IRequest<Result<IReadOnlyList<SlaConfigDto>>>;
