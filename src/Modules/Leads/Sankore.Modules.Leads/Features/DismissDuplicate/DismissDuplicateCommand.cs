namespace Sankore.Modules.Leads.Features.DismissDuplicate;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

internal sealed record DismissDuplicateCommand(
    Guid TenantId,
    /// <summary>The lead from whose context the duplicate search was performed.</summary>
    Guid LeadId,
    /// <summary>The candidate lead that the agent has determined is NOT a duplicate.</summary>
    Guid CandidateLeadId,
    Guid DismissedBy,
    string? Reason = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
