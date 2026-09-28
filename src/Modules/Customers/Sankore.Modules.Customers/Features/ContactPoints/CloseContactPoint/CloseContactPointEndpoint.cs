using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.ContactPoints.Shared;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Features.ContactPoints.CloseContactPoint;

public static class CloseContactPointEndpoint
{
    public static IEndpointRouteBuilder MapCloseContactPoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{contactPointId:guid}", Handle)
            .WithName("CloseClientContactPoint")
            .WithSummary("Close a client contact point")
            .WithDescription(
                "Dates the contact point's validTo; the row is never physically deleted and stays " +
                "readable with includeClosed=true. Closing the last active phone is refused with " +
                "LAST_PHONE_REQUIRED. When the closed contact point was the primary one, the oldest " +
                "remaining active contact point of the same type is promoted. " +
                "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        Guid contactPointId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CloseContactPointCommand(clientId, contactPointId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : ContactPointHttp.ToProblem(result.Error);
    }
}
