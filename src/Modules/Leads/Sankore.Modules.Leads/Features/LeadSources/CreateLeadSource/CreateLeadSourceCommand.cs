namespace Sankore.Modules.Leads.Features.LeadSources.CreateLeadSource;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record CreateLeadSourceCommand(
    Guid TenantId,
    string Code,
    string Label,
    int DisplayOrder,
    string? Description = null
) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "LeadSourceConfig";
    public string? ResourceId  => null;
}
