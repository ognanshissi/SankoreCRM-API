namespace Sankore.Modules.Leads.Features.PipelineStages.ListPipelineStages;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListPipelineStagesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<PipelineStageConfigDto>>>;
