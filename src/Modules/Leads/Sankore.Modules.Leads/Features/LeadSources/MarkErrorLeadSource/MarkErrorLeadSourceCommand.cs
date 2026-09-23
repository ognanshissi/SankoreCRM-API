namespace Sankore.Modules.Leads.Features.LeadSources.MarkErrorLeadSource;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record MarkErrorLeadSourceCommand(Guid SourceId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => SourceId.ToString();
}
