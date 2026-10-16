namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.UnlinkUser;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UnlinkTelephonyUserEndpoint
{
    internal static IEndpointRouteBuilder MapUnlinkTelephonyUser(this IEndpointRouteBuilder app)
    {
        // POST and not DELETE: the act takes a mandatory body — the reason — and a DELETE with a
        // body is a request several proxies and HTTP clients drop silently. The row is not
        // deleted either, which the route name says out loud.
        app.MapPost("user-links/{linkId:guid}/unlink", Handle)
            .WithName("UnlinkTelephonyUser")
            .WithSummary("Switch a user's provider link off, with a reason")
            .WithDescription(
                "The link is DISABLED, never deleted: the agent's calls already in the journal "
                + "point at it, and it is what explains why nothing new is attributed to them. "
                + "The reason is mandatory and audited — an agent leaving, an account recycled "
                + "and a mistaken pairing are three different facts. Unlinking an already "
                + "unlinked link is a success. Linking the same pair again revives this row. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid linkId,
        UnlinkTelephonyUserRequest request,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(new UnlinkTelephonyUserCommand(linkId, request.Reason), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            TelephonyErrors.UserLinkNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

internal sealed record UnlinkTelephonyUserRequest(string Reason);
