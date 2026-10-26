namespace Sankore.Modules.Leads.Features.Opportunities.OpenHolding;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Opens — or finds — the draft contract of a WON project. The closing already tries once and
/// swallows a failure; this is how the agent tries again and learns why it did not work.
/// </summary>
internal sealed record OpenProjectHoldingCommand(Guid OpportunityId)
    : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}
