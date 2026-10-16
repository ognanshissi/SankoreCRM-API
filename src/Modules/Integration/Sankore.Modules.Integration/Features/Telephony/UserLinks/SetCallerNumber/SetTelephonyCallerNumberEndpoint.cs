namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.SetCallerNumber;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class SetTelephonyCallerNumberEndpoint
{
    internal static IEndpointRouteBuilder MapSetTelephonyCallerNumber(this IEndpointRouteBuilder app)
    {
        app.MapPut("user-links/{linkId:guid}/caller-number", Handle)
            .WithName("SetTelephonyCallerNumber")
            .WithSummary("The number this user's outbound calls present")
            .WithDescription(
                "E.164 only (a leading + and 8 to 15 digits): the value is handed to the "
                + "provider's API verbatim, and a local form is refused there rather than here. "
                + "A null or blank value CLEARS the override and falls the user back on the "
                + "connection's default caller number, which is the ordinary state. Audited. "
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
        SetTelephonyCallerNumberRequest request,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new SetTelephonyCallerNumberCommand(linkId, request.DefaultCallerNumber), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            TelephonyErrors.UserLinkNotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}

internal sealed record SetTelephonyCallerNumberRequest(string? DefaultCallerNumber);
