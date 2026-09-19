namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record UpdateLeadOwnerCommand(
    Guid LeadId,
    Guid OwnerId,
    Guid AssignedBy,
    /// <summary>How the assignment was triggered: Manual | Import | System.</summary>
    string AssignmentMethod = "Manual",
    string? Reason = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
