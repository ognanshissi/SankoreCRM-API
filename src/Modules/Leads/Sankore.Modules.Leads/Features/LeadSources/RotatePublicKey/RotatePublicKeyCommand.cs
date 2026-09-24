namespace Sankore.Modules.Leads.Features.LeadSources.RotatePublicKey;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record RotatePublicKeyCommand(Guid SourceId)
    : IRequest<Result<string>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
