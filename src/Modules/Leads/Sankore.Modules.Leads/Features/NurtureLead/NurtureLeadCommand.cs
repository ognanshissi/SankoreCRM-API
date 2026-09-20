namespace Sankore.Modules.Leads.Features.NurtureLead;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>Moves an active lead into the Nurturing state and enrolls into a sequence.</summary>
internal sealed record NurtureLeadCommand(
    Guid LeadId,
    Guid? SequenceId = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
