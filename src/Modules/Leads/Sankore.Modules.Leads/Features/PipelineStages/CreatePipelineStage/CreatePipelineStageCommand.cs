namespace Sankore.Modules.Leads.Features.PipelineStages.CreatePipelineStage;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreatePipelineStageCommand(
    Guid TenantId,
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null,
    string? Color = null,
    bool IsFinal = false
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "PipelineStageConfig";
    public string? ResourceId  => null;
}
