namespace Sankore.Modules.Leads.Features.RecycleLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Recycles a Lost, Disqualified or Nurturing lead back into the active pipeline.
/// The lead's score is reset to 0 and must be re-qualified.
/// </summary>
internal sealed record RecycleLeadCommand(
    Guid LeadId,
    LeadSource? NewSource = null,
    string? NewCampaign = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
