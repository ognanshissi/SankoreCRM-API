namespace Sankore.Modules.Leads.Features.RecordFirstContact;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records the date/time of the agent's first contact with a lead,
/// stopping the SLA clock on the active assignment.
/// </summary>
internal sealed record RecordFirstContactCommand(
    Guid LeadId,
    DateTimeOffset? ContactedAt = null   // defaults to now if omitted
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId  => LeadId.ToString();
}
