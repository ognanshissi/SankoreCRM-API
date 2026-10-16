namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class LinkTelephonyUserEndpoint
{
    internal static IEndpointRouteBuilder MapLinkTelephonyUser(this IEndpointRouteBuilder app)
    {
        app.MapPost("user-links", Handle)
            .WithName("LinkTelephonyUser")
            .WithSummary("Confirm that a CRM user owns a provider account")
            .WithDescription(
                "Takes the pairing GET telephony/directory proposed, or one typed by hand. One "
                + "CRM user holds at most one account and one account at most one CRM user: a "
                + "live link on either side answers 409 TELEPHONY_USER_LINK_ALREADY_TAKEN rather "
                + "than moving the account, because moving it silently re-attributes every call "
                + "that follows. Confirming a link that already exists is a SUCCESS, and it "
                + "revives one that was broken or unlinked. Audited. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces<LinkTelephonyUserResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        LinkTelephonyUserRequest request,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new LinkTelephonyUserCommand(
                request.CrmUserId,
                request.ExternalUserId,
                request.ExternalEmail,
                request.DefaultCallerNumber),
            ct);

        if (result.IsSuccess)
            return Results.Ok(new LinkTelephonyUserResponse(result.Value));

        return result.Error switch
        {
            TelephonyErrors.UserLinkAlreadyTaken => Results.Conflict(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

/// <summary>
/// The pairing to confirm. <c>ExternalEmail</c> is what the provider lists for the account and is
/// kept so a later reader can see what the proposal was based on; it is never used to re-derive
/// the link.
/// </summary>
internal sealed record LinkTelephonyUserRequest(
    Guid CrmUserId,
    string ExternalUserId,
    string? ExternalEmail,
    string? DefaultCallerNumber);

internal sealed record LinkTelephonyUserResponse(Guid LinkId);
