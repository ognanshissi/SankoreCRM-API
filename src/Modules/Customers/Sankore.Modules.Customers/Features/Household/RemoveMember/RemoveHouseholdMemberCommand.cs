namespace Sankore.Modules.Customers.Features.Household.RemoveMember;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Removes a member from a client's household (BNF-B-13) — LOGICALLY: the row is closed with a
/// reason, never deleted. A household that forgets a separation cannot answer who was covered when
/// a claim was filed.
/// </summary>
/// <param name="Reason">
/// Required. A separation and a data-entry error are not the same event, and a removal nobody can
/// explain is one a reviewer cannot act on.
/// </param>
public sealed record RemoveHouseholdMemberCommand(
    Guid ClientId,
    Guid MemberId,
    string Reason
) : IRequest<Result<RemoveHouseholdMemberResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Client";
    public string? ResourceId => ClientId.ToString();
}

/// <summary>
/// <paramref name="DesignationsToReview"/> is how many beneficiary designations the departing
/// member held and now need a decision (BNF-B-13). Returned so the screen that removed them can
/// say so immediately, rather than leaving the agent to discover it on a holding.
/// </summary>
public sealed record RemoveHouseholdMemberResult(
    int DesignationsToReview,
    IReadOnlyList<Guid> AffectedHoldingIds);
