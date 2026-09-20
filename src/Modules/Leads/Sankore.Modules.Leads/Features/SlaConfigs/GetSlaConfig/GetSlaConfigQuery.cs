namespace Sankore.Modules.Leads.Features.SlaConfigs.GetSlaConfig;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetSlaConfigQuery(Guid SlaConfigId)
    : IRequest<Result<SlaConfigDto>>;
