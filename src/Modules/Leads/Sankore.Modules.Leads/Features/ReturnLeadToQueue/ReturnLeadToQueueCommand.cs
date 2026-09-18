namespace Sankore.Modules.Leads.Features.ReturnLeadToQueue;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Unassigns a dispatched lead and returns it to the Qualified queue
/// so it can be re-dispatched to a different agent.
/// </summary>
internal sealed record ReturnLeadToQueueCommand(Guid LeadId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
