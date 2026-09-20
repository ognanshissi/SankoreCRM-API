namespace Sankore.Modules.Leads.Features.PipelineStages.ActivatePipelineStage;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ActivatePipelineStageCommand(Guid StageId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "PipelineStageConfig";
    public string? ResourceId  => StageId.ToString();
}
