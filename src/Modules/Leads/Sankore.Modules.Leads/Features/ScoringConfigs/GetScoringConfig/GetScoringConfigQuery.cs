namespace Sankore.Modules.Leads.Features.ScoringConfigs.GetScoringConfig;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetScoringConfigQuery(Guid ConfigId)
    : IRequest<Result<ScoringConfigDto>>;
