using MediatR;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Features.Analytics.GetTemplateStats;

internal sealed record GetTemplateStatsQuery(Guid TemplateId)
    : IRequest<Result<TemplateStatsDto>>;
