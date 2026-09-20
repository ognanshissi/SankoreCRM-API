namespace Sankore.Modules.Leads.Features.LeadSources.UpdateLeadSource;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateLeadSourceCommand(
    Guid SourceId,
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
