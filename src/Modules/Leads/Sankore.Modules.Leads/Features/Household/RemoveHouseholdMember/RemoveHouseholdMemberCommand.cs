namespace Sankore.Modules.Leads.Features.Household.RemoveHouseholdMember;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Removes a household member LOGICALLY (BNF-B-03) — never a delete.
/// </summary>
/// <param name="Reason">
/// Required. A separation, a death and a data-entry error all produce the same row otherwise, and
/// BNF-B-13 has a removed member's designations move to review rather than vanish — whoever
/// reviews them needs to know why the member went.
/// </param>
internal sealed record RemoveHouseholdMemberCommand(
    Guid LeadId,
    Guid MemberId,
    string Reason,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}
