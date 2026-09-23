namespace Sankore.Modules.Leads.Features.LeadSources.PauseLeadSource;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record PauseLeadSourceCommand(Guid SourceId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
