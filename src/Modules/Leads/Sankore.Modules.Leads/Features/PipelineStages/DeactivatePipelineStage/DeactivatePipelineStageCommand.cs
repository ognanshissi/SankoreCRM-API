namespace Sankore.Modules.Leads.Features.PipelineStages.DeactivatePipelineStage;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DeactivatePipelineStageCommand(Guid StageId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "PipelineStageConfig";
    public string? ResourceId  => StageId.ToString();
}
