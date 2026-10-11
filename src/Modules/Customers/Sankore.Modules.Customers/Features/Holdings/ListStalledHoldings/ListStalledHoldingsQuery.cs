namespace Sankore.Modules.Customers.Features.Holdings.ListStalledHoldings;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>
/// Signed holdings that have been queued for a back office for longer than they should, across
/// the whole tenant (BNF-B-16).
///
/// <para>
/// <b>Why this exists.</b> A subscription whose insurer cannot be determined — no
/// <c>integration_mapping</c> row for the product line, several active insurance connections —
/// is refused inside <c>HoldingSubscriptionConsumer</c>, which catches the refusal and writes ONE
/// warning line. The holding then sits in <c>PendingSync</c> for ever with nothing to move it,
/// and the agent's only symptom is a subscription that never completes.
/// </para>
///
/// <para>
/// Nothing could see that state. The reconciliation run (BNF-B-17) enumerates the CRM side from
/// <c>integration_reference</c>, and a holding that was never queued has no reference — the same
/// structural blindness that makes <c>MissingInCrm</c> undetectable, declared rather than papered
/// over. <c>ListHoldings</c> could not see it either: it takes a <c>ClientId</c>, so the question
/// "which of my institution's subscriptions are stuck" had no form.
/// </para>
///
/// <para>
/// The companion preflight is Integration's <c>product-routing</c>, which says which lines WILL
/// fail. This says which holdings already did.
/// </para>
/// </summary>
/// <param name="MinAgeHours">
/// How long a holding must have been in <c>PendingSync</c> to count as stalled. A few minutes is
/// normal — the command is queued and the dispatcher is asynchronous — so a default of zero would
/// report every healthy subscription in flight.
/// </param>
internal sealed record ListStalledHoldingsQuery(int MinAgeHours, int Page, int PageSize)
    : IRequest<Result<PagedResult<StalledHoldingDto>>>;

/// <param name="ClientId">
/// Carried so the row is actionable: the holding's own id acts on it, the client's id opens the
/// file. No name and no amount — this is an operations list read by whoever fixes mappings, and
/// it has no reason to carry the client's identity.
/// </param>
/// <param name="StalledSinceHours">
/// Hours since the holding entered <c>PendingSync</c>, rounded down. Sorted oldest first: the
/// longest-stuck subscription is the one a client has been waiting on.
/// </param>
public sealed record StalledHoldingDto(
    Guid HoldingId,
    Guid ClientId,
    Guid ProductLineId,
    string ProductCode,
    string ProductCategory,
    DateTimeOffset? SignedAt,
    DateTimeOffset UpdatedAt,
    int StalledSinceHours);
