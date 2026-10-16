namespace Sankore.Modules.Integration.Features.Telephony.LinkCall;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.CallContext;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Attaches a call to a record, by hand (TEL-B-09).
///
/// <para>
/// <b>Both SIDES are authorized, and neither check is optional.</b> The permission says this
/// caller may attach calls; it says nothing about WHICH calls or to WHOSE records — and the two
/// gaps are different leaks. An unauthorized CALL would let an agent re-file a colleague's
/// conversation, so a call is movable only by its own agent, by whoever supervises the agency it
/// belonged to, or by anybody when nobody owns it. An unauthorized
/// TARGET is the worse of the two: the call then appears in that record's history, which
/// <c>GetCallsForEntityAsync</c> projects with no perimeter of its own, so an agent of one branch
/// could file their conversation into another branch's client file — or pull another branch's
/// calls into a client of their own and read back who spoke to whom and for how long. That is the
/// defect M01's <c>DesignationSubjectValidator</c> exists for, where "any non-empty Guid was
/// stored".
/// </para>
///
/// <para>
/// <b>A supervisor MAY correct their team's call, which they could not until TEL-B-19.</b> The
/// call side was "mine, or nobody's" and nothing else, because <c>tel_call</c> carried an agent
/// and no agency — so "is this call inside my perimeter" had no answer, and inventing one by
/// resolving the agent's branch per request would have been a policy written in a handler. The
/// agency is now on the row, stamped with the branch its agent was in AT THE TIME, so the manager
/// who may fix a mis-filed conversation is the one who was answerable for it when it happened.
/// The target side is unchanged and still decides on its own: covering the call grants no right
/// over the record it is being filed into.
/// </para>
///
/// <para>
/// <b>Where the reason is kept: the audit row.</b> <c>AuditBehavior</c> serialises an
/// <c>ICommand</c>'s payload, so the reason, the author and the instant are recorded together
/// under the permission that gates reading the audit trail — and that is the right place for it
/// rather than a column on <c>tel_call</c>. A column would hold the LAST reason only, while what
/// a later reader needs is the sequence: a call moved from one client to another and back is a
/// different story from one attached once.
/// </para>
/// </summary>
internal sealed class LinkCallHandler(
    IntegrationDbContext db,
    TelephonyRecordAccess recordAccess,
    TelephonyCallPerimeter perimeter,
    ICurrentUser currentUser,
    ILogger<LinkCallHandler> logger,
    IDistributedCache? cache = null)
    : IRequestHandler<LinkCallCommand, Result>
{
    public async Task<Result> Handle(LinkCallCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var call = await db.TelCalls
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.CallId, ct);

        if (call is null)
            return Result.Fail(TelephonyErrors.CallNotFound);

        // ── The call side ────────────────────────────────────────────────────
        // The caller's own call, or one nobody owns — which is the "calls to attribute" queue a
        // whole agency works, and the case that needs attaching most. Folded into "not found"
        // rather than answered 403: a 403 would confirm that a colleague spoke to somebody.
        //
        // A colleague's call falls to the agency perimeter: a branch manager correcting their own
        // team's mis-filing is the case this exists for, and it is asked of the agency the call
        // belonged to at the time rather than of where its agent sits today.
        if (call.CrmUserId is { } owner
            && owner != currentUser.Id
            && !await perimeter.CoversAsync(
                currentUser.TenantId, currentUser.Id, call.AgencyId, ct))
        {
            logger.LogWarning(
                "User {UserId} of tenant {TenantId} tried to re-attach call {CallId}, which "
                + "belongs to another agent and is outside their perimeter.",
                currentUser.Id, currentUser.TenantId, call.Id);

            return Result.Fail(TelephonyErrors.CallNotFound);
        }

        // ── The target side ──────────────────────────────────────────────────
        // Asked of the module that owns the record, because the perimeter is theirs. Fails closed
        // on an outage: a check that did not happen must not read as a pass.
        var mayAct = await recordAccess.CanActOnAsync(
            currentUser.TenantId, cmd.EntityType, cmd.EntityId, currentUser.Id, ct);

        if (!mayAct)
        {
            // The entity id is logged and nothing of the record is: this line is read while
            // somebody investigates, and the id is what they need to look it up under their own
            // permissions.
            logger.LogWarning(
                "User {UserId} of tenant {TenantId} may not attach call {CallId} to "
                + "{EntityType} {EntityId}.",
                currentUser.Id, currentUser.TenantId, call.Id, cmd.EntityType, cmd.EntityId);

            return Result.Fail(TelephonyErrors.EntityNotYours);
        }

        // Manual wins over anything automatic — that is what correcting a mistake means — and the
        // aggregate owns the rule. It cannot fail for a Manual method, so the only refusals
        // reachable here are the two shape ones the validator already covers; relayed anyway,
        // because a handler that assumed otherwise would be wrong the day the rule changes.
        var attached = call.Attach(
            TelMatchStatus.Matched, TelMatchMethod.Manual, cmd.EntityType, cmd.EntityId);

        if (attached.IsFailure)
            return attached;

        await db.SaveChangesAsync(ct);

        // The context read caches the resolution for sixty seconds, PER USER — so this agent's
        // own entry is the one that would otherwise keep showing them the two names they have
        // just chosen between. Evicted AFTER the save, which is the ordering a cache must have:
        // evicting first opens a window for a concurrent read to repopulate from the pre-write
        // row, and that is what makes a cache permanently wrong rather than briefly stale.
        await EvictAsync(call.Id, ct);

        logger.LogInformation(
            "Call {CallId} of tenant {TenantId} attached by hand to {EntityType} {EntityId} by "
            + "user {UserId}.",
            call.Id, currentUser.TenantId, cmd.EntityType, cmd.EntityId, currentUser.Id);

        return Result.Ok();
    }

    private async Task EvictAsync(Guid callId, CancellationToken ct)
    {
        if (cache is null) return;

        try
        {
            await cache.RemoveAsync(
                GetCallContextHandler.CacheKey(currentUser.TenantId, currentUser.Id, callId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The entry expires in under a minute either way. Failing the attachment over a cache
            // eviction would lose the agent's correction to keep a cache tidy.
            logger.LogDebug(ex, "Could not evict the telephony context cache entry.");
        }
    }
}
