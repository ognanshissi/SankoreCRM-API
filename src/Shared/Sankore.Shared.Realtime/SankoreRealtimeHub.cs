namespace Sankore.Shared.Realtime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

/// <summary>
/// The platform's single hub. Mounted at <see cref="Path"/> by the host (TEL-B-10).
///
/// <para>
/// <b>One hub and not one per concern</b>, which departs from the specification's
/// <c>TelephonyHub</c>. Each hub costs the same four things — a route exempted from tenant
/// resolution, a query-string token exemption on the JWT handler, a CORS entry and a group-naming
/// convention — and every one of them is a place a second copy can be subtly different. The
/// concern lives in the EVENT NAME (<c>telephony.call.ringing</c>) rather than in the URL, and a
/// browser subscribes to what it cares about. Adding presence or a notification toast later is
/// then a new event name and no new infrastructure.
/// </para>
///
/// <para>
/// <b>It has no methods a client can call, deliberately.</b> Everything flows server to browser.
/// A hub method is a public, authenticated RPC surface with no route, no rate-limit policy and no
/// OpenAPI entry — which makes it the one surface in this platform a reviewer would never see. A
/// browser that needs to ask something calls the HTTP API, where the permission, the perimeter
/// and the audit already live.
/// </para>
///
/// <para>
/// <b>Group membership is the whole authorization model.</b> A connection joins exactly one group,
/// derived from the claims on its own token, and nothing can ask to join another: the group name
/// is computed here from <see cref="Context"/> and is never taken from the client. A push is
/// therefore addressed to a (tenant, user) pair and cannot be addressed to a tenant.
/// </para>
/// </summary>
[Authorize]
public sealed class SankoreRealtimeHub(ILogger<SankoreRealtimeHub> logger) : Hub
{
    /// <summary>
    /// The route the host maps and the browser connects to. A constant because three places must
    /// agree: the mapping, the JWT handler's query-string exemption, and the tenant-resolution
    /// exemption list. A mistyped copy in any of them is a connection that fails with no useful
    /// message.
    /// </summary>
    public const string Path = "/hubs/realtime";

    /// <summary>
    /// The group one (tenant, user) pair's connections share.
    ///
    /// <para>
    /// The tenant is in the name for the reason it is in every Redis key here: the backplane is
    /// one shared channel, and a group named after a user id alone would deliver one tenant's
    /// pushes to another's if the two ever held the same identifier. It is also what makes
    /// <see cref="IRealtimeNotifier.PushToUserAsync"/>'s tenant parameter load-bearing rather
    /// than decorative.
    /// </para>
    /// </summary>
    internal static string UserGroup(Guid tenantId, Guid userId) => $"t:{tenantId:D}:u:{userId:D}";

    public override async Task OnConnectedAsync()
    {
        var identity = RealtimeIdentity.From(Context.User);

        if (identity is null)
        {
            // Should not happen behind [Authorize], and is not treated as impossible: a token
            // that authenticates but carries no tenant claim would otherwise join the group of
            // Guid.Empty — the SYSTEM placeholder — and receive whatever a background job pushed
            // there. Aborted instead, and said out loud.
            logger.LogWarning(
                "A realtime connection authenticated without a usable tenant and user claim; "
                + "aborted.");

            Context.Abort();

            return;
        }

        await Groups.AddToGroupAsync(
            Context.ConnectionId, UserGroup(identity.TenantId, identity.UserId));

        // Debug and not information: an agency of forty agents reconnects on every page load and
        // on every network blip, and at information level this line alone would be the bulk of a
        // day's logs. No identifier of a person beyond the user id, which is already in the log
        // scope.
        logger.LogDebug(
            "Realtime connection {ConnectionId} joined user {UserId} of tenant {TenantId}.",
            Context.ConnectionId, identity.UserId, identity.TenantId);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Nothing to do on disconnect, and that is not an omission: SignalR removes a gone
    /// connection from its groups itself. A manual <c>RemoveFromGroupAsync</c> here would be a
    /// call against a connection that no longer exists.
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            logger.LogDebug(
                exception, "Realtime connection {ConnectionId} dropped.", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
