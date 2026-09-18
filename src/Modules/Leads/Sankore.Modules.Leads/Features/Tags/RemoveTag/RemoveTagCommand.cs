namespace Sankore.Modules.Leads.Features.Tags.RemoveTag;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record RemoveTagCommand(Guid LeadId, string Tag)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId  => LeadId.ToString();
}
