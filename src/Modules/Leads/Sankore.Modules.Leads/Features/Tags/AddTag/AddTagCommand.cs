namespace Sankore.Modules.Leads.Features.Tags.AddTag;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record AddTagCommand(Guid LeadId, string Tag, Guid AddedBy)
    : IRequest<Result<TagDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId  => LeadId.ToString();
}

public sealed record TagDto(Guid Id, string Tag, Guid AddedBy, DateTimeOffset AddedAt);
