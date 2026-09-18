namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateLeadOwnerCommand(Guid LeadId, Guid OwnerId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
