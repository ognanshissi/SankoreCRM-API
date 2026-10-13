namespace Sankore.Modules.Kyc.Features.PublicRemote.LinkStatus;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Features.PublicRemote;

/// <summary>
/// What a link is, answered to whoever holds it (KRM-B-05).
///
/// <para>
/// The first call the public page makes, and the one that records that the message reached a human.
/// It is a <b>POST</b> for a read, deliberately: the token would otherwise be a query-string value,
/// and a query string lands in nginx access logs, in a proxy's logs and in a <c>Referer</c> — the
/// three places the whole fragment-based link design exists to keep a bearer credential out of.
/// A GET here would undo that in one line.
/// </para>
///
/// <para>
/// It carries NO personal data and nothing about the customer: not a name, not an initial, not an
/// agency. Only the state of the link and, when it is usable, when it stops being usable. Everything
/// about the file waits for the session.
/// </para>
/// </summary>
internal static class KycLinkStatusEndpoint
{
    internal static IEndpointRouteBuilder MapKycLinkStatus(this IEndpointRouteBuilder app)
    {
        app.MapPost("link/status", Handle)
            .WithName("GetKycRemoteLinkStatus")
            .WithSummary("Whether a remote-collection link can still be used")
            .WithDescription(
                "Anonymous, rate-limited. POST and not GET so the token never reaches a query "
                + "string, which would put it in access logs and Referer headers. Answers Usable, "
                + "Expired, Consumed, Revoked, Locked, FileClosed or Unknown — and nothing about "
                + "the customer. Marks the link as opened the first time it is called. A token "
                + "presented on another tenant's domain answers 403.")
            .AllowAnonymous()
            .Produces<KycLinkStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        KycLinkStatusRequest request,
        KycRemoteGate gate,
        CancellationToken ct)
    {
        var resolution = await gate.ResolveLinkAsync(request?.Token, track: true, ct);

        if (resolution.State == KycRemoteGate.LinkState.WrongTenant)
        {
            // 403 and not 404: the link is real, it simply does not belong to this institution.
            // Saying so is what stops a customer from typing their client number into somebody
            // else's branding, which is the attack this check exists for.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new KycLinkStatusResponse(
            Status: resolution.State.ToString(),
            // Only for a link that still works. On a dead link it would be a date the customer can
            // do nothing with, and on an unknown token it would be an invention.
            ExpiresAt: resolution.IsUsable ? resolution.Invitation!.ExpiresAt : null));
    }
}

/// <param name="Token">
/// The base64url value from the link's fragment. The page reads it from <c>location.hash</c> and
/// wipes it from the address bar before sending it here.
/// </param>
public sealed record KycLinkStatusRequest(string? Token);

/// <param name="Status">
/// <c>Usable</c> | <c>Expired</c> | <c>Consumed</c> | <c>Revoked</c> | <c>Locked</c> |
/// <c>FileClosed</c> | <c>Unknown</c>. The front turns it into a screen; the states are
/// distinguished because each one sends the customer somewhere different, and a single "invalide"
/// produces a phone call for every one of them.
/// </param>
/// <param name="ExpiresAt">Null unless the link is usable.</param>
public sealed record KycLinkStatusResponse(string Status, DateTimeOffset? ExpiresAt);
