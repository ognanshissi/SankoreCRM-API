namespace Sankore.Modules.Integration.Features.Telephony.CallContext;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Resolves one ringing call into something an agent's screen can show (TEL-B-10).
///
/// <para>
/// <b>Under 500 ms at p95 is the story's requirement and it drives the shape.</b> The expensive
/// part is not this module's row — that is a primary-key read — it is asking M01 and then M13 to
/// resolve the remote number, which is two cross-module round trips through two blind indexes. So
/// the resolution is cached in Redis for sixty seconds, and sixty is not arbitrary: it is longer
/// than a telephone rings and shorter than the time in which a lead could be captured for that
/// number, so a stale entry can only ever be the same answer.
/// </para>
///
/// <para>
/// <b>The cache key carries the TENANT and the USER.</b> The tenant because Redis is one shared
/// instance and a key built from a call id alone would be a cross-tenant read. The user because
/// the answer IS per user: two agents of two branches asking about the same call are entitled to
/// different names, and a key without the user would serve whichever of them asked first — which
/// is a perimeter leak that only shows up under load.
/// </para>
/// </summary>
internal sealed class GetCallContextHandler(
    IntegrationDbContext db,
    TelephonyCallMatcher matcher,
    TelephonyNumberProtector numbers,
    IAgencyScopeProvider agencyScope,
    ICurrentUser currentUser,
    ILogger<GetCallContextHandler> logger,
    IDistributedCache? cache = null)
    : IRequestHandler<GetCallContextQuery, Result<TelephonyCallContext>>
{
    /// <summary>
    /// Sixty seconds — see the type's remarks for why that number and not a longer one.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<Result<TelephonyCallContext>> Handle(
        GetCallContextQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tenantId = currentUser.TenantId;

        // No tenant predicate: the global query filter scopes it, so another tenant's call is
        // simply not found — never a 403, which would confirm the call exists.
        var call = await db.TelCalls
            .Where(c => c.Id == query.CallId)
            .Select(c => new
            {
                c.Id, c.Direction, c.Status, c.MatchStatus, c.StartedAt,
                c.EntityType, c.EntityId, c.FromNumberEnc, c.ToNumberEnc,
            })
            .FirstOrDefaultAsync(ct);

        if (call is null)
            return Result.Fail<TelephonyCallContext>(TelephonyErrors.CallNotFound);

        var cacheKey = CacheKey(tenantId, currentUser.Id, call.Id);

        var cached = await ReadCacheAsync(cacheKey, ct);

        if (cached is not null)
        {
            return Result.Ok(new TelephonyCallContext(
                call.Id, call.Direction.ToString(), call.Status.ToString(),
                call.MatchStatus.ToString(), cached, call.StartedAt));
        }

        var candidates = await ResolveAsync(tenantId, call.EntityType, call.EntityId,
            call.Direction, call.FromNumberEnc, call.ToNumberEnc, ct);

        await WriteCacheAsync(cacheKey, candidates, ct);

        return Result.Ok(new TelephonyCallContext(
            call.Id, call.Direction.ToString(), call.Status.ToString(),
            call.MatchStatus.ToString(), candidates, call.StartedAt));
    }

    /// <summary>
    /// Who the call might be about, filtered to what this caller may be told.
    ///
    /// <para>
    /// An ATTACHED call answers its own attachment and does not re-resolve the number: that
    /// attachment may have been made by a human (TEL-B-09's manual correction) or by the
    /// click-to-call the agent themselves started, and re-resolving would quietly replace a
    /// decision with a lookup.
    /// </para>
    ///
    /// <para>
    /// The name of a record OUTSIDE the perimeter is withheld and the candidate is still listed.
    /// That is deliberate and is the one place this screen says more than a 404 would: the agent
    /// learns the caller is known to the institution elsewhere, which stops them searching for
    /// somebody they will never find, while the record itself stays hidden. What leaks is the
    /// existence of a match, which the ringing telephone has already established.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<TelephonyCandidate>> ResolveAsync(
        Guid tenantId,
        string? entityType,
        Guid? entityId,
        TelCallDirection direction,
        byte[] fromNumberEnc,
        byte[] toNumberEnc,
        CancellationToken ct)
    {
        var scope = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, currentUser.Id, ct);

        if (entityType is not null && entityId is { } attachedId)
        {
            // One candidate, and the name resolved the same way the unattached path resolves it:
            // by asking the owning module about the number. There is no "get the name of this
            // record" on either contract that a screen pop may use, and adding one would be a
            // second way to read a name — so the attachment is matched against the resolution
            // instead, and an attachment whose record no longer answers degrades to no name.
            var resolved = await ResolveByNumberAsync(tenantId, direction, fromNumberEnc, toNumberEnc, ct);

            var match = resolved.FirstOrDefault(
                c => c.EntityType == entityType && c.EntityId == attachedId);

            return
            [
                new TelephonyCandidate(
                    entityType,
                    attachedId,
                    Visible(match?.AgencyId, scope) ? match?.DisplayName : null,
                    OutOfPerimeter: !Visible(match?.AgencyId, scope)),
            ];
        }

        var candidates = await ResolveByNumberAsync(tenantId, direction, fromNumberEnc, toNumberEnc, ct);

        return
        [
            .. candidates.Select(c => new TelephonyCandidate(
                c.EntityType,
                c.EntityId,
                Visible(c.AgencyId, scope) ? c.DisplayName : null,
                OutOfPerimeter: !Visible(c.AgencyId, scope))),
        ];
    }

    private async Task<IReadOnlyList<ResolvedCandidate>> ResolveByNumberAsync(
        Guid tenantId,
        TelCallDirection direction,
        byte[] fromNumberEnc,
        byte[] toNumberEnc,
        CancellationToken ct)
    {
        // The REMOTE end — the caller for an inbound call, the callee for an outbound one. The
        // other end is the institution's own line and would resolve to nobody, or worse to
        // whichever client happens to share a number with a branch.
        var remote = numbers.Reveal(
            direction == TelCallDirection.Inbound ? fromNumberEnc : toNumberEnc);

        if (remote is null)
        {
            // An anonymised call (the numbers are blanked and the row kept) or a key problem. Both
            // answer "nobody", which is what the screen shows for a withheld caller id anyway.
            logger.LogDebug("Telephony context: the remote number could not be read.");

            return [];
        }

        return await matcher.ResolveCandidatesAsync(tenantId, remote, ct);
    }

    /// <summary>
    /// Whether a record in <paramref name="agencyId"/> may be NAMED to this caller.
    ///
    /// <para>
    /// A null scope is an unrestricted caller — a super-user or a tenant-wide account — and
    /// <c>IAgencyScopeProvider</c> is explicit that it means "apply no filter" and never "no
    /// access". A null AGENCY is the opposite: a lead nobody has assigned sits in no perimeter,
    /// so it is named only to a caller who has none either.
    /// </para>
    /// </summary>
    private static bool Visible(Guid? agencyId, IReadOnlySet<Guid>? scope)
    {
        if (scope is null) return true;

        return agencyId is { } agency && scope.Contains(agency);
    }

    /// <summary>
    /// <c>integration:telephony:context:{tenant}:{user}:{call}</c> — the module's
    /// <c>module:concern:id</c> shape, with the two discriminators the remarks above explain.
    /// </summary>
    internal static string CacheKey(Guid tenantId, Guid userId, Guid callId)
        => $"integration:telephony:context:{tenantId}:{userId}:{callId}";

    private async Task<IReadOnlyList<TelephonyCandidate>?> ReadCacheAsync(
        string key, CancellationToken ct)
    {
        if (cache is null) return null;

        try
        {
            var bytes = await cache.GetAsync(key, ct);

            return bytes is null
                ? null
                : JsonSerializer.Deserialize<List<TelephonyCandidate>>(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cache is an optimisation. Redis being unreachable must cost a screen pop its
            // latency budget, never its answer.
            logger.LogDebug(ex, "Telephony context cache read failed; resolving live.");

            return null;
        }
    }

    private async Task WriteCacheAsync(
        string key, IReadOnlyList<TelephonyCandidate> candidates, CancellationToken ct)
    {
        if (cache is null) return;

        try
        {
            await cache.SetAsync(
                key,
                JsonSerializer.SerializeToUtf8Bytes(candidates),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheFor },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Telephony context cache write failed; the answer stands.");
        }
    }
}
