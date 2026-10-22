namespace Sankore.Modules.Integration.Features.Telephony.MyStatus;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>The account that was linked, as the provider names it.</summary>
internal sealed record LinkMyTelephonyAccountResponse(Guid LinkId, string? DisplayName);

/// <summary>
/// « Lier mon compte » — an agent pairs THEMSELVES with their provider account, without an
/// administrator.
///
/// <para>
/// <b>The e-mail address is the proof of ownership, and the only one.</b> Pairing attributes every
/// call that follows — the customer's number, the agent's note, the recording — so an agent
/// choosing an account from a list would be an agent able to read a colleague's calls. The account
/// is therefore never named by the caller: it is the one provider account whose address is the
/// address of exactly ONE CRM user, that user being the caller. Two provider accounts with the
/// address, or two CRM users sharing it, and nothing is linked — an administrator decides, through
/// the screen that already exists for it.
/// </para>
///
/// <para>
/// The pairing itself goes through <see cref="LinkTelephonyUserCommand"/>, so the uniqueness rules,
/// the revival of a disabled row and the audit are the administrator's path, not a copy of it. The
/// one difference is upstream: that command trusts its caller to have checked, this one checks.
/// </para>
/// </summary>
internal sealed record LinkMyTelephonyAccountCommand : IRequest<Result<LinkMyTelephonyAccountResponse>>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyUserLink";
    public string? ResourceId => null;
}

internal sealed class LinkMyTelephonyAccountHandler(
    TelephonyAccess access,
    IAdministrationModule administration,
    ICurrentUser currentUser,
    ISender sender,
    ILogger<LinkMyTelephonyAccountHandler> logger)
    : IRequestHandler<LinkMyTelephonyAccountCommand, Result<LinkMyTelephonyAccountResponse>>
{
    public async Task<Result<LinkMyTelephonyAccountResponse>> Handle(LinkMyTelephonyAccountCommand cmd, CancellationToken ct)
    {
        var use = await access.ResolvePortAsync(currentUser.TenantId, IntegrationCapability.UserDirectory, ct);
        if (use.IsFailure) return Result.Fail<LinkMyTelephonyAccountResponse>(use.Code!);

        var users = await use.Value.Port.ListUsersAsync(ct);
        if (users.IsFailure)
        {
            logger.LogWarning("Self-link: telephony directory unavailable for tenant {TenantId} ({Code}).", currentUser.TenantId, users.Code);
            return Result.Fail<LinkMyTelephonyAccountResponse>(IntegrationErrors.Unavailable);
        }

        var withEmail = users.Value.Where(u => !string.IsNullOrWhiteSpace(u.Email)).ToList();
        var matches = await administration.FindUsersByEmailAsync(
            currentUser.TenantId, withEmail.Select(u => u.Email!.Trim()).ToList(), ct);

        // The addresses that are MINE: those I hold, alone, in the CRM.
        var mine = matches
            .GroupBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(m => m.UserId == currentUser.Id))
            .ToList();

        if (mine.Any(g => g.Count() > 1))
            return Result.Fail<LinkMyTelephonyAccountResponse>(TelephonyErrors.SelfLinkAmbiguous);

        var myEmails = mine.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = withEmail.Where(u => u.IsActive && myEmails.Contains(u.Email!.Trim())).ToList();

        if (candidates.Count == 0) return Result.Fail<LinkMyTelephonyAccountResponse>(TelephonyErrors.SelfLinkNoMatch);
        if (candidates.Count > 1) return Result.Fail<LinkMyTelephonyAccountResponse>(TelephonyErrors.SelfLinkAmbiguous);

        var account = candidates[0];
        var linked = await sender.Send(new LinkTelephonyUserCommand(currentUser.Id, account.Id.Value, account.Email), ct);

        return linked.IsSuccess
            ? Result.Ok(new LinkMyTelephonyAccountResponse(linked.Value, account.DisplayName))
            : Result.Fail<LinkMyTelephonyAccountResponse>(linked.Error!);
    }
}

internal static class LinkMyTelephonyAccountEndpoint
{
    internal static IEndpointRouteBuilder MapLinkMyTelephonyAccount(this IEndpointRouteBuilder app)
    {
        app.MapPost("me/link", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new LinkMyTelephonyAccountCommand(), ct);
                if (result.IsSuccess) return Results.Ok(result.Value);

                return result.Error switch
                {
                    TelephonyErrors.UserLinkAlreadyTaken => Results.Conflict(new { error = result.Error }),
                    IntegrationErrors.Unavailable => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),
                    _ => Results.UnprocessableEntity(new { error = result.Error }),
                };
            })
            .WithName("LinkMyTelephonyAccount")
            .WithSummary("Pairs YOU with your provider account, found by your e-mail address")
            .WithDescription(
                "No body: the account is the provider account whose address belongs to exactly one "
                + "CRM user, you. TELEPHONY_SELF_LINK_NO_MATCH when none does, "
                + "TELEPHONY_SELF_LINK_AMBIGUOUS when two accounts or two users share it — an "
                + "administrator then pairs by hand. 409 TELEPHONY_USER_LINK_ALREADY_TAKEN when the "
                + "account is someone else's. Audited. Needs no permission beyond being signed in.")
            .RequireAuthorization()
            .Produces<LinkMyTelephonyAccountResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }
}
