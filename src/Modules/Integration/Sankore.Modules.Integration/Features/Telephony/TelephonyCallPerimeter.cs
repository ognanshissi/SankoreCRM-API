namespace Sankore.Modules.Integration.Features.Telephony;

using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// "Is this call inside my perimeter" — the question <c>tel_call</c> could not answer until
/// TEL-B-19 put an agency on it.
///
/// <para>
/// <b>Two surfaces needed it and neither could ask.</b> Correcting an attachment (TEL-B-09)
/// refused every call but the caller's own, so a branch manager could not fix a mis-filed
/// conversation of their own team — stated as a deliberate gap at the time, because guessing a
/// perimeter by resolving the agent's branch per request would have been a policy invented in a
/// handler. Listening to a recording (TEL-B-17) could only ask about the ATTACHED RECORD, so a
/// colleague's call that was never attached was audible to compliance alone: correct, but strict
/// enough that a supervisor investigating a complaint had no route to the conversation it was
/// about. Both now ask here.
/// </para>
///
/// <para>
/// <b>The agency is the call's own, which is the agency its agent sat in at the time.</b> So the
/// manager who may review a call is the one who managed it when it happened — an agent
/// transferred in March does not take February's conversations to their new branch, and does not
/// take them away from the manager who was answerable for them.
/// </para>
///
/// <para>
/// <b>It answers ONLY the agency question.</b> "My own call" and "nobody's call" are deliberately
/// left to the callers: the two surfaces mean different things by an unattributed call — a queue
/// to work in one, a conversation to hear in the other — and folding them in here would make one
/// of the two rules invisible at the place it applies.
/// </para>
/// </summary>
internal sealed class TelephonyCallPerimeter(
    IAgencyScopeProvider agencyScope,
    ILogger<TelephonyCallPerimeter> logger)
{
    /// <summary>
    /// <b>Fails CLOSED.</b> An authorization question, so the posture is the access guard's and
    /// not the capture path's: an outage refuses and the supervisor retries, where swallowing it
    /// would let a perimeter check that never happened read as a pass.
    /// </summary>
    internal async Task<bool> CoversAsync(
        Guid tenantId, Guid actorUserId, Guid? callAgencyId, CancellationToken ct)
    {
        try
        {
            var scope = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, actorUserId, ct);

            // Null is UNRESTRICTED — a super-user or a tenant-wide account — and the contract is
            // explicit that it must be read as "apply no agency filter", never as "no access".
            // Getting it backwards here would lock out exactly the people whose job this is.
            if (scope is null) return true;

            // A call with no agency is inside no restricted perimeter. It is not hidden from
            // everybody: whoever sees the tenant whole has already passed the arm above, and the
            // caller's own "mine, or nobody's" arm covers the one case a colleague needs.
            return callAgencyId is { } agencyId && scope.Contains(agencyId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Could not establish whether user {ActorUserId} of tenant {TenantId} covers "
                  + "agency {AgencyId}; refused.",
                actorUserId, tenantId, callAgencyId);

            return false;
        }
    }
}
