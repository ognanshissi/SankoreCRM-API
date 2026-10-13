namespace Sankore.Modules.Kyc.Features.RemoteInvitations;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Modules.Kyc.Features.RemoteInvitations.ListInvitations;
using Sankore.Modules.Kyc.Features.RemoteInvitations.ReissueInvitation;
using Sankore.Modules.Kyc.Features.RemoteInvitations.RevokeInvitation;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Area aggregator for the back-office side of remote collection (KRM-B-04). The three endpoints
/// live in one file rather than one per slice: each is a handful of lines, and the three are read
/// together — "what links exist, issue one, withdraw one" is a single screen's worth of API.
///
/// <para>
/// The PUBLIC endpoints the customer's own page calls are deliberately not here. They are
/// anonymous, rate-limited and mounted under <c>api/v1/public/kyc</c>, and keeping them in their
/// own area is what stops an authenticated-by-default group from ever covering them by accident.
/// </para>
/// </summary>
internal static class RemoteInvitationsEndpoints
{
    internal static IEndpointRouteBuilder MapKycRemoteInvitationsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("kyc-files").WithTags("KYC");

        group.MapGet("{kycFileId:guid}/remote-invitations", ListAsync)
            .WithName("ListKycRemoteInvitations")
            .WithSummary("The remote-collection links issued for a KYC file, and their progress")
            .WithDescription(
                "Carries no token and no hash: there is deliberately no way to learn what a link "
                + "was. `unavailableReason` says why no new link can be issued — "
                + "KYC_REMOTE_INVITATION_NO_EMAIL, KYC_REMOTE_INVITATION_PRODUCT_NOT_ELIGIBLE or "
                + "KYC_REMOTE_INVITATION_FILE_NOT_COLLECTING — and is re-evaluated on every read, "
                + "so it disappears as soon as the cause is fixed. Requires permission: kyc:read.")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycRemoteInvitationListDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{kycFileId:guid}/remote-invitations", IssueAsync)
            .WithName("IssueKycRemoteInvitation")
            .WithSummary("Send or re-send the remote-collection link of a KYC file")
            .WithDescription(
                "Revokes the link currently in force, if any, and issues a new one — at most one "
                + "link into a file is ever live. The email goes out immediately (unlike the one "
                + "the conversion schedules, which waits for the welcome message to land first). "
                + "Refused with KYC_REMOTE_INVITATION_NO_EMAIL, "
                + "KYC_REMOTE_INVITATION_PRODUCT_NOT_ELIGIBLE or "
                + "KYC_REMOTE_INVITATION_FILE_NOT_COLLECTING, and with KYC_FILE_NOT_FOUND for a "
                + "file outside the caller's agency perimeter — never 403, which would confirm "
                + "the file exists. Requires permission: kyc:remote-invite.")
            .RequireAuthorization(Permissions.CanInviteKycRemote.Code)
            .Produces<IssueKycRemoteInvitationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{kycFileId:guid}/remote-invitations/{invitationId:guid}/revoke", RevokeAsync)
            .WithName("RevokeKycRemoteInvitation")
            .WithSummary("Withdraw a remote-collection link")
            .WithDescription(
                "The reason is mandatory and is kept on the link: a customer's way into their own "
                + "file disappearing with no recorded motive is something a support call cannot "
                + "close. Revoking an already spent link succeeds and changes nothing. Requires "
                + "permission: kyc:remote-invite.")
            .RequireAuthorization(Permissions.CanInviteKycRemote.Code)
            .Accepts<RevokeKycRemoteInvitationRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid kycFileId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListKycRemoteInvitationsQuery(kycFileId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> IssueAsync(
        Guid kycFileId, ISender sender, CancellationToken ct)
    {
        // ReissueKycRemoteInvitationCommand and NOT IssueKycRemoteInvitationCommand: the agent
        // path has to pass the agency perimeter, and the scoped command is the only one this
        // endpoint can reach. See that command for the IDOR this split closes.
        var result = await sender.Send(new ReissueKycRemoteInvitationCommand(kycFileId), ct);

        if (result.IsFailure)
        {
            return result.Error switch
            {
                KycErrors.FileNotFound or KycErrors.RemoteInvitationNotFound
                    => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error }),
            };
        }

        return Results.Ok(new IssueKycRemoteInvitationResponse(
            result.Value.InvitationId, result.Value.ExpiresAt, result.Value.Reused));
    }

    private static async Task<IResult> RevokeAsync(
        Guid kycFileId,
        Guid invitationId,
        RevokeKycRemoteInvitationRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RevokeKycRemoteInvitationCommand(kycFileId, invitationId, req.Reason), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                KycErrors.FileNotFound or KycErrors.RemoteInvitationNotFound
                    => Results.NotFound(new { error = result.Error }),
                _ => Results.BadRequest(new { error = result.Error }),
            };
    }
}

/// <param name="Reused">
/// True when a usable link already existed and nothing new was sent. Never true for this endpoint
/// — an agent always replaces — but carried because it is part of the command's answer and a
/// client reading it should not have to know which caller it came from.
/// </param>
public sealed record IssueKycRemoteInvitationResponse(
    Guid InvitationId,
    DateTimeOffset ExpiresAt,
    bool Reused);

/// <param name="Reason">Mandatory, 5 to 2000 characters. Read by an agent, never a field value.</param>
public sealed record RevokeKycRemoteInvitationRequest(string Reason);
