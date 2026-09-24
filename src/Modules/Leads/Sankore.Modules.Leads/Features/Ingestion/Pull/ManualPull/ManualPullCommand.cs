namespace Sankore.Modules.Leads.Features.Ingestion.Pull.ManualPull;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record ManualPullCommand(Guid SourceId)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
