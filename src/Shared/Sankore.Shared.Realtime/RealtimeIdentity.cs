namespace Sankore.Shared.Realtime;

using System.Security.Claims;

/// <summary>
/// The (tenant, user) pair a hub connection speaks for, read from its own token.
///
/// <para>
/// A type of its own rather than two <c>Guid.Parse</c> calls in the hub, because the hub's
/// correctness rests on exactly one thing: the group a connection joins is derived from its token
/// and from nothing a client sent. Parsing in one named place is what lets that be read in a
/// review.
/// </para>
///
/// <para>
/// <b>It parses and never throws.</b> <c>HttpContextCurrentUser</c> may use <c>Guid.Parse</c> on
/// these same claims because it runs inside a request that a policy has already vetted; a hub's
/// <c>OnConnectedAsync</c> runs on a connection whose token authenticated but whose claim SHAPE
/// is not something any policy asserted — and an exception there is a connection that fails with
/// no diagnosis on either end.
/// </para>
/// </summary>
internal sealed record RealtimeIdentity(Guid TenantId, Guid UserId)
{
    /// <summary>The same claim names <c>HttpContextCurrentUser</c> reads. Drift here is silent.</summary>
    private const string TenantClaim = "tenant_id";

    internal static RealtimeIdentity? From(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;

        if (!Guid.TryParse(principal.FindFirst(TenantClaim)?.Value, out var tenantId)
            || tenantId == Guid.Empty)
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
            || userId == Guid.Empty)
        {
            return null;
        }

        return new RealtimeIdentity(tenantId, userId);
    }
}
