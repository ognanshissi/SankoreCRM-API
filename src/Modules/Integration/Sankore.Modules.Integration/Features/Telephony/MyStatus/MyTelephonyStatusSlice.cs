namespace Sankore.Modules.Integration.Features.Telephony.MyStatus;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// "Does my telephony work?" — asked by the agent whose telephony it is (TEL-F-11).
///
/// <para>
/// <b>It exists because every other read that could answer it is gated behind an administrator's
/// permission</b>, which is precisely the wrong person. <c>GET telephony/user-links</c> needs
/// <c>Telephony.Settings.Manage</c> and the connection list needs
/// <c>Integration.Connection.Manage</c>, so the one user who needs to know why their Appeler
/// button is missing is the one user who cannot find out. Without this, a front end would have to
/// learn the answer by attempting a call and reading the refusal code — feature detection by
/// error, which puts a failed command in the journal every time a screen loads.
/// </para>
///
/// <para>
/// <b>No permission beyond being signed in, deliberately.</b> It exposes nothing but the caller's
/// own state: whether their institution has telephony, whether their own account is paired, and
/// their own caller number. There is no other user's data to leak and no id a caller could
/// substitute — the subject is always <c>ICurrentUser.Id</c>, never a parameter.
/// </para>
/// </summary>
internal sealed record GetMyTelephonyStatusQuery : IRequest<Result<MyTelephonyStatusDto>>;

/// <param name="TelephonyEnabled">
/// Whether the institution has telephony at all. <b>The ACTIVE connection is the switch</b> —
/// <c>TelephonyAccess</c> argues at length why there is no second flag — so this is false for the
/// great majority of tenants, which have no telephony and for whom the whole area should simply
/// not appear.
/// </param>
/// <param name="LinkStatus">
/// <c>NotLinked</c>, or one of <c>TelUserLinkStatus</c>: <c>Active</c>, <c>Broken</c>,
/// <c>Disabled</c>. Four values and not a boolean, because the repairs differ: nobody has paired
/// this agent yet, the provider stopped listing their account, or an administrator unpaired them
/// with a reason.
/// </param>
/// <param name="DefaultCallerNumber">
/// The number this agent's outbound calls present, when their link names one. The one place a
/// telephone number is returned by this family — and it is the institution's own line, not a
/// customer's, which is the whole difference.
/// </param>
/// <param name="CanMakeCalls">
/// The single flag a screen needs to decide whether to render an Appeler button: telephony is on,
/// this agent is paired, and they hold the permission. Computed here rather than left to the
/// front to recombine, so three screens cannot each get it slightly wrong.
/// </param>
/// <param name="ConnectionId">
/// For an administrator looking at their own account while debugging a deployment. Null when
/// telephony is off.
/// </param>
internal sealed record MyTelephonyStatusDto(
    bool TelephonyEnabled,
    string LinkStatus,
    string? ExternalUserId,
    string? DefaultCallerNumber,
    bool CanMakeCalls,
    Guid? ConnectionId);

internal sealed class GetMyTelephonyStatusHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    IHttpContextAccessor httpContextAccessor)
    : IRequestHandler<GetMyTelephonyStatusQuery, Result<MyTelephonyStatusDto>>
{
    /// <summary>"Nobody has paired this agent", which is not a <c>TelUserLinkStatus</c> value.</summary>
    internal const string NotLinked = "NotLinked";

    public async Task<Result<MyTelephonyStatusDto>> Handle(
        GetMyTelephonyStatusQuery query, CancellationToken ct)
    {
        var me = currentUser.Id;

        // Read straight from the connection row rather than through TelephonyAccess, and the
        // reason is the shape of this endpoint's traffic: the top bar reads it on every page load
        // for every agent, while TelephonyAccess resolves an ADAPTER from the container — work
        // this question does not need. Activation already requires a passing health check, so an
        // active row means the adapter answered at least once.
        //
        // No tenant predicate: the global query filter scopes it.
        var connection = await db.Connections
            .Where(c => c.Family == IntegrationFamily.Telephony && c.IsActive)
            .Select(c => new { c.Id })
            .FirstOrDefaultAsync(ct);

        if (connection is null)
        {
            // Telephony is off for this institution. Answered as a successful "no" and never as a
            // 404: the agent's screen has something true to display, and the great majority of
            // tenants live in this branch permanently.
            return Result.Ok(new MyTelephonyStatusDto(
                TelephonyEnabled: false,
                LinkStatus: NotLinked,
                ExternalUserId: null,
                DefaultCallerNumber: null,
                CanMakeCalls: false,
                ConnectionId: null));
        }

        var link = await db.TelUserLinks
            .Where(l => l.ConnectionId == connection.Id && l.CrmUserId == me)
            .Select(l => new { l.Status, l.ExternalUserId, l.DefaultCallerNumber })
            .FirstOrDefaultAsync(ct);

        // Read off the token's own claims, which is how AuthorizationPolicies renders every
        // permission — so this cannot drift from what the Appeler endpoint would have checked.
        var mayMake = httpContextAccessor.HttpContext?.User.HasClaim(
            "permission", Permissions.CanMakeTelephonyCall.Code) == true;

        return Result.Ok(new MyTelephonyStatusDto(
            TelephonyEnabled: true,
            LinkStatus: link?.Status.ToString() ?? NotLinked,
            ExternalUserId: link?.ExternalUserId,
            DefaultCallerNumber: link?.DefaultCallerNumber,

            // A BROKEN link may still place a call — the provider stopped listing the account,
            // which is a settings row to repair, not a reason to stop an agent mid-shift — but it
            // is reported as Broken so the indicator can say so. Only a DISABLED link, where
            // somebody stated a reason, stops the button.
            CanMakeCalls: mayMake
                          && link is not null
                          && link.Status != TelUserLinkStatus.Disabled,
            ConnectionId: connection.Id));
    }
}

internal static class MyTelephonyStatusEndpoint
{
    internal static IEndpointRouteBuilder MapMyTelephonyStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("me", Handle)
            .WithName("GetMyTelephonyStatus")
            .WithSummary("Whether YOUR telephony works, and why not")
            .WithDescription(
                "The one telephony read an ordinary agent can make about themselves. Every other "
                + "route that could answer it is gated behind an administrator's permission, "
                + "which is the wrong person: the user who needs to know why their Appeler button "
                + "is missing is exactly the user who cannot look. "
                + "telephonyEnabled is false when the institution has no ACTIVE telephony "
                + "connection — which is most tenants, and the whole area should then be hidden. "
                + "linkStatus is NotLinked, Active, Broken or Disabled: four values and not a "
                + "boolean, because the repairs differ (nobody paired you, the provider stopped "
                + "listing your account, an administrator unpaired you with a reason). "
                + "canMakeCalls combines all three conditions — telephony on, you are paired, you "
                + "hold Telephony.Call.Make — so a screen has one flag to read instead of three "
                + "to recombine. A Broken link still allows calls; only Disabled stops them. "
                + "Exposes nothing but your own state, so it needs no permission beyond being "
                + "signed in.")
            .RequireAuthorization()
            .Produces<MyTelephonyStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new GetMyTelephonyStatusQuery(), ct)).Value);
}
