namespace Sankore.Modules.Kyc.Features.PublicRemote;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Shared.Kernel;

/// <summary>
/// How a link, or a session, becomes a KYC file the public endpoints may write to — and the two
/// checks that stand in the way (KRM-B-05, §4.3, §8).
///
/// <para>
/// One service because these endpoints are the product's ONLY anonymous write surface, and every
/// one of them needs the same four things: resolve the token to an invitation without leaking
/// whether it exists, confirm the invitation is still usable against the clock rather than against
/// a status a nightly job happens to have updated, confirm the FILE is still collecting, and
/// confirm the tenant the invitation belongs to is the tenant whose domain the request arrived on.
/// Spread across five handlers, one of those four would be missing from one of them.
/// </para>
///
/// <para>
/// <b>Why the tenant check lives here and not in the middleware.</b>
/// <c>TenantResolutionMiddleware</c> exempts <c>/api/v1/public</c> wholesale — it runs before
/// routing and those surfaces carry their own tenant reference — so nothing upstream compares the
/// token's tenant with the host. Without this, a link leaked from one institution would open on
/// another institution's domain, under its logo, and the customer would type their client number
/// into it.
/// </para>
/// </summary>
internal sealed class KycRemoteGate(
    KycDbContext db,
    IKycRemoteLinkTokens tokens,
    IKycRemoteSessions sessions,
    ITenantStore tenantStore,
    IHttpContextAccessor accessor,
    TimeProvider clock,
    ILogger<KycRemoteGate> logger)
{
    /// <summary>
    /// What a link is, as told to whoever holds it.
    ///
    /// <para>
    /// The states ARE distinguished, which looks like an information leak and is not: answering
    /// requires presenting the token, so the only thing learned is the state of a link the caller
    /// already has. KRM-B-05 asks for it explicitly, and the reason is support — "expiré" sends
    /// the customer to their agency, "déjà utilisé" tells them their file is already in, and a
    /// single "invalide" for all of it produces a phone call for each.
    /// </para>
    /// </summary>
    internal enum LinkState
    {
        /// <summary>No such token, or not a token of ours at all.</summary>
        Unknown,
        Usable,
        Expired,
        Consumed,
        Revoked,
        Locked,

        /// <summary>
        /// The token is valid but its FILE has moved on — an agent took over at the counter. Its
        /// own state because the customer must not be told to keep uploading, and must not be told
        /// their link was revoked either: nothing was taken from them.
        /// </summary>
        FileClosed,

        /// <summary>
        /// The token belongs to another institution than the domain it was presented on. Reported
        /// separately so the log can say so; the endpoint answers 403.
        /// </summary>
        WrongTenant,
    }

    internal sealed record LinkResolution(LinkState State, KycRemoteInvitation? Invitation)
    {
        internal bool IsUsable => State == LinkState.Usable && Invitation is not null;
    }

    /// <summary>
    /// Resolves a token presented by an anonymous caller.
    /// </summary>
    /// <param name="track">
    /// Whether to record that somebody opened the link. True for the status probe the page makes on
    /// load, false for the identification attempt that follows — otherwise one customer's single
    /// visit would be counted twice and <c>OpenedAt</c> would mean "last touched" instead of
    /// "first opened".
    /// </param>
    internal async Task<LinkResolution> ResolveLinkAsync(
        string? token, bool track, CancellationToken ct)
    {
        // Shape-checked before it is hashed, so a flood of junk never becomes a flood of queries.
        var hash = tokens.HashOrNull(token);
        if (hash is null)
            return new LinkResolution(LinkState.Unknown, null);

        // IgnoreQueryFilters with no tenant predicate, and this is the one place in the module
        // that does it. There IS no ambient tenant on an anonymous request — the middleware
        // exempts this path — and the token is what identifies the tenant. The unique index on
        // token_hash is global for exactly this reason, so the row that comes back carries its own
        // tenant and the check below is what validates it.
        var invitation = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);

        if (invitation is null)
            return new LinkResolution(LinkState.Unknown, null);

        if (!await TenantMatchesHostAsync(invitation.TenantId, ct))
            return new LinkResolution(LinkState.WrongTenant, null);

        var state = Classify(invitation);

        if (state != LinkState.Usable)
            return new LinkResolution(state, invitation);

        // The FILE has the last word: a link is only as alive as what it collects for.
        var fileStatus = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == invitation.TenantId && f.Id == invitation.KycFileId)
            .Select(f => (KycFileStatus?)f.Status)
            .FirstOrDefaultAsync(ct);

        if (fileStatus is not (KycFileStatus.Collecting or KycFileStatus.ComplementRequired))
            return new LinkResolution(LinkState.FileClosed, invitation);

        if (track && invitation.MarkOpened(clock))
            await db.SaveChangesAsync(ct);

        return new LinkResolution(LinkState.Usable, invitation);
    }

    /// <summary>
    /// The session behind the current request, resolved all the way to a live invitation.
    ///
    /// <para>
    /// <b>This is what "the session is revoked" means.</b> A JWT cannot be withdrawn once signed,
    /// so a session is not trusted on its signature alone: every call re-reads the invitation and
    /// stops the moment it is no longer usable. A customer who submits, or whose link an agent
    /// revokes mid-journey, loses their session on their very next request — without a revocation
    /// list to keep, and without a 20-minute window in which a spent link still writes.
    /// </para>
    /// </summary>
    internal async Task<LinkResolution> ResolveSessionAsync(CancellationToken ct)
    {
        if (sessions.Current() is not { } session)
            return new LinkResolution(LinkState.Unknown, null);

        if (!await TenantMatchesHostAsync(session.TenantId, ct))
            return new LinkResolution(LinkState.WrongTenant, null);

        var invitation = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                i => i.TenantId == session.TenantId
                  && i.Id == session.InvitationId
                  && i.KycFileId == session.KycFileId, ct);

        if (invitation is null)
            return new LinkResolution(LinkState.Unknown, null);

        var state = Classify(invitation);

        // Identified is the normal state of a link whose session is in use; Classify already
        // treats it as usable, and the window check it applies is the link's, which is the outer
        // bound of the session's own lifetime.
        if (state != LinkState.Usable)
            return new LinkResolution(state, invitation);

        var fileStatus = await db.KycFiles
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == invitation.TenantId && f.Id == invitation.KycFileId)
            .Select(f => (KycFileStatus?)f.Status)
            .FirstOrDefaultAsync(ct);

        return fileStatus is KycFileStatus.Collecting or KycFileStatus.ComplementRequired
            ? new LinkResolution(LinkState.Usable, invitation)
            : new LinkResolution(LinkState.FileClosed, invitation);
    }

    /// <summary>
    /// Whether <paramref name="tenantId"/> is the tenant whose domain this request arrived on.
    ///
    /// <para>
    /// <b>An absent header passes.</b> The token is the credential, and refusing a request that
    /// simply did not say which domain it came from would break every non-browser client — a
    /// curl reproduction, a monitoring probe, the Angular app before its interceptor is wired. What
    /// this closes is the case that matters and that the spec names: a token PRESENTED on another
    /// institution's domain, which is how a leaked link would be walked under the wrong logo.
    /// A mismatch is logged as a security event, because it has no innocent explanation.
    /// </para>
    /// </summary>
    private async Task<bool> TenantMatchesHostAsync(Guid tenantId, CancellationToken ct)
    {
        var fqdn = accessor.HttpContext?.Request.Headers["X-Tenant-Fqdn"].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(fqdn))
            return true;

        var fromHost = await tenantStore.GetByFqdnAsync(fqdn, ct);

        // An unrecognised domain is not a mismatch: it is a request from somewhere we do not host,
        // and the token still says which tenant it belongs to.
        if (fromHost is null)
            return true;

        if (fromHost.Id == tenantId)
            return true;

        logger.LogWarning(CrossTenantLinkReplay,
            "Remote KYC link of tenant {TokenTenantId} presented on the domain of tenant "
            + "{HostTenantId} ({Fqdn})",
            tenantId, fromHost.Id, fqdn);

        return false;
    }

    /// <summary>
    /// Its own event id rather than <c>SecurityEvents.CrossTenantTokenReplay</c>, which is
    /// <c>internal</c> to <c>Sankore.Shared.Infrastructure</c> and therefore unreachable from a
    /// module. The number is deliberately in the same range so an operator filtering on security
    /// events finds both — and the two are genuinely the same incident seen from two layers: the
    /// middleware catches a replayed USER token, this catches a replayed LINK.
    /// </summary>
    private static readonly EventId CrossTenantLinkReplay =
        new(4302, "CrossTenantRemoteKycLinkReplay");

    /// <summary>
    /// The link's own state, from its status and the clock.
    ///
    /// <para>
    /// The CLOCK and not only the status: a link's window closes the instant it passes, while the
    /// status only becomes <c>Expired</c> when the nightly sweep runs. Trusting the status alone
    /// would leave a bearer credential working for up to a day past its stated lifetime.
    /// </para>
    /// </summary>
    private LinkState Classify(KycRemoteInvitation invitation) => invitation.Status switch
    {
        KycRemoteInvitationStatus.Submitted => LinkState.Consumed,
        KycRemoteInvitationStatus.Revoked => LinkState.Revoked,
        KycRemoteInvitationStatus.Locked => LinkState.Locked,
        KycRemoteInvitationStatus.Expired => LinkState.Expired,

        // Scheduled cannot be reached by a caller — a scheduled link has no token yet, so the hash
        // probe above cannot match it — but it is spelled out rather than left to the fallback, so
        // that a future state added to the enum fails loudly here instead of silently becoming
        // usable.
        KycRemoteInvitationStatus.Scheduled
            or KycRemoteInvitationStatus.Sent
            or KycRemoteInvitationStatus.Opened
            or KycRemoteInvitationStatus.Identified =>
            clock.GetUtcNow() < invitation.ExpiresAt ? LinkState.Usable : LinkState.Expired,

        _ => LinkState.Unknown,
    };
}
