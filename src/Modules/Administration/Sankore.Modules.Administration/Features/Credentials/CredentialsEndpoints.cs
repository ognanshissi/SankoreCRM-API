namespace Sankore.Modules.Administration.Features.Credentials;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Credentials.CloseCredential;
using Sankore.Modules.Administration.Features.Credentials.ExpiringCredentials;
using Sankore.Modules.Administration.Features.Credentials.IssueCredential;
using Sankore.Modules.Administration.Features.Credentials.ListCredentials;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Professional cards — the <i>carte professionnelle</i> of CIMA articles 503 and 510, the
/// professional indemnity cover of Book V, and the training hours of Book VII article 732.
///
/// <para>
/// Two shapes, because the questions differ: a card is recorded and read under its HOLDER, while
/// "what is about to lapse" is a tenant-wide report with no user in its route.
/// </para>
///
/// <para>
/// <b>No route returns a card number.</b> It identifies a named person on a regulator's register,
/// and nothing an operator does with these rows requires reading it back.
/// </para>
/// </summary>
public static class CredentialsEndpoints
{
    public static IEndpointRouteBuilder MapCredentialsEndpoints(this IEndpointRouteBuilder app)
    {
        var perUser = app.MapGroup("users/{userId:guid}/credentials")
            .WithTags("Distribution Credentials");

        perUser.MapGet(string.Empty, List)
            .WithName("ListUserCredentials")
            .WithSummary("A user's professional cards, current first")
            .WithDescription(
                "The full history: superseded and withdrawn rows are returned, because the "
                + "question a controller asks is what was valid on a past date. The card number "
                + "is never included. Requires permission: user:read.")
            // user:read, not credential:manage: a screen must be able to warn an agent that their
            // card expires next month without granting the right to issue one.
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<IReadOnlyList<CredentialDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        perUser.MapPost(string.Empty, Issue)
            .WithName("IssueUserCredential")
            .WithSummary("Record or renew a professional card")
            .WithDescription(
                "A renewal CLOSES the holder's current card in the same transaction and requires "
                + "`supersedeReason` — a register that cannot tell a lapse from a withdrawal "
                + "answers none of the questions it exists for. Refuses with "
                + "CREDENTIAL_CARD_NUMBER_ALREADY_RECORDED when another holder currently has that "
                + "number. Audited; the number is recorded by field name only. "
                + "Requires permission: user:credential:manage.")
            .RequireAuthorization(Permissions.CanManageDistributionCredentials.Code)
            .Produces<CredentialDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        // POST on a noun, not DELETE: the row is closed, never removed.
        perUser.MapPost("withdrawal", Close)
            .WithName("WithdrawUserCredential")
            .WithSummary("Withdraw a professional card without replacing it")
            .WithDescription(
                "For a suspension by the authority, a departure, or a card handed back. The row "
                + "is closed with its reason and kept. The holder then stops being eligible for "
                + "insurance dispatch and for signing a subscription. "
                + "Requires permission: user:credential:manage.")
            .RequireAuthorization(Permissions.CanManageDistributionCredentials.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapGet("distribution-credentials/expiring", Expiring)
            .WithName("ListExpiringCredentials")
            .WithTags("Distribution Credentials")
            .WithSummary("Cards that have lapsed or are about to")
            .WithDescription(
                "Current cards expiring within `withinDays` (default 60), soonest first, plus "
                + "any whose recorded professional indemnity cover has lapsed — the half an "
                + "institution does not watch, and which invalidates the credential even while "
                + "the card is in date. Read it before the dispatching guard surprises somebody: "
                + "an agent whose card expires silently stops receiving insurance leads. "
                + "Requires permission: user:read.")
            .RequireAuthorization(Permissions.CanReadUser.Code)
            .Produces<IReadOnlyList<ExpiringCredentialDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> List(Guid userId, ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ListCredentialsQuery(userId), ct)).Value);

    private static async Task<IResult> Issue(
        Guid userId, IssueCredentialRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new IssueCredentialCommand(
            UserId: userId,
            Category: body.Category,
            CardNumber: body.CardNumber,
            IssuingAuthority: body.IssuingAuthority,
            IssuedOn: body.IssuedOn,
            ExpiresOn: body.ExpiresOn,
            IndemnityInsurer: body.IndemnityInsurer,
            IndemnityPolicyReference: body.IndemnityPolicyReference,
            IndemnityExpiresOn: body.IndemnityExpiresOn,
            TrainingHours: body.TrainingHours,
            TrainingCompletedOn: body.TrainingCompletedOn,
            SupersedeReason: body.SupersedeReason), ct);

        if (result.IsSuccess)
        {
            return Results.Created(
                $"/api/v1/users/{userId}/credentials", result.Value);
        }

        return result.Error switch
        {
            CredentialErrors.UserNotFound => Results.NotFound(new { error = result.Error }),
            CredentialErrors.DuplicateCardNumber => Results.Conflict(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }

    private static async Task<IResult> Close(
        Guid userId, CloseCredentialRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CloseCredentialCommand(userId, body.Reason), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error == CredentialErrors.NotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Expiring(
        int? withinDays, ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ExpiringCredentialsQuery(withinDays ?? 60), ct)).Value);
}
