namespace Sankore.Modules.Leads.Features.UpdatePipelineStage;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdatePipelineStageCommand(Guid LeadId, PipelineStage NewStage)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
