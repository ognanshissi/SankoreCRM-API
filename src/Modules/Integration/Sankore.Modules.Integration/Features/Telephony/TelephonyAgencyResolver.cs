namespace Sankore.Modules.Integration.Features.Telephony;

using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// "Which agency was this agent in" — asked of M12, once, when a call is journalled (TEL-B-19).
///
/// <para>
/// <b>Why the answer is copied onto the call rather than joined at read time.</b> The agency is
/// the second axis of the indicators and it is also what finally gives "is this call inside my
/// perimeter" an answer. Both questions are about the past. A join to the agent's CURRENT agency
/// would answer a different one: an agent transferred in March would move February's figures into
/// their new branch, and February's conversations with them — so last month's numbers would
/// change after the fact, which is the one thing a manager reading a report cannot forgive.
/// </para>
///
/// <para>
/// <b>It answers null on an outage, and null is the SAFE direction here.</b> Null means "outside
/// every restricted perimeter": it never grants a supervisor access they would not otherwise
/// have, and it buckets the call under "no agency" in the indicators rather than under somebody
/// else's branch. The call is not lost either — <c>TelCall.StampAgency</c> is retried on every
/// later journal reading of a row that still has none, and a call produces two to four of those,
/// so an M12 blip during a ring is repaired by the next webhook.
/// </para>
/// </summary>
internal sealed class TelephonyAgencyResolver(
    IAdministrationModule administration,
    ILogger<TelephonyAgencyResolver> logger)
{
    internal async Task<Guid?> ResolveAsync(Guid tenantId, Guid? crmUserId, CancellationToken ct)
    {
        // No agent attributed: nothing to resolve, and no reason to trouble M12. An unattributed
        // call has no agency by construction, not by failure.
        if (crmUserId is not { } userId || userId == Guid.Empty) return null;

        try
        {
            // Suppressed, like every cross-module read on this module's write paths:
            // TransactionBehavior holds a System.Transactions scope for the HTTP callers and the
            // other module reads through its own DbContext, so a second connection enlisting
            // would promote the ambient transaction to a distributed one — which Npgsql does not
            // support. Harmless where there is no ambient transaction (the webhook, the jobs).
            using var suppressed = new TransactionScope(
                TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

            return await administration.GetUserAgencyAsync(tenantId, userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Warning and not error: the row is usable, the indicators bucket it as "no agency",
            // and the next reading of the same call tries again. An error here would page
            // somebody for something that self-heals in seconds.
            logger.LogWarning(
                ex, "Could not resolve the agency of user {CrmUserId} in tenant {TenantId}; the "
                  + "call is journalled without one and the next reading will retry.",
                userId, tenantId);

            return null;
        }
    }
}
