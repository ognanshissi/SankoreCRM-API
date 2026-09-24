namespace Sankore.Modules.Leads.Features.LeadSources.Snippet;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record SendSnippetCommand(Guid SourceId, string RecipientEmail)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
